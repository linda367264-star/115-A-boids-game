using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace BoidArena
{
    // ================================================================
    //  守護者型 Boid
    //  1. 敵人從背後逼近 → 側閃＋轉頭面對它（正面或側面相撞只會彈開，不會死）
    //  2. 隊友被敵人從背後追 → 附近的我方繞到那個敵人的背後反殺
    //  3. 有五芒星、食物就去吃，優先於其他行為
    //     （例外：敵人已經貼在背後、下一刻就會被殺，這時先閃，見 FoodOverridesEmergency）
    //
    //  ※ 隊名：Maris
    //  ※ 這個類別「完全不存狀態」：引擎不論是一隻鳥一個實例，或全隊共用一個實例都沒問題
    // ================================================================
    public class Boid_Maris : IBrain, IDeclareRelations
    {
        public string Name { get { return "Maris"; } }

        // ---------------- 可調參數（全部是常數，不是可變的 static 欄位） ----------------

        // true：連「敵人貼在背後」都不管，照樣先吃（完全照字面的「吃最優先」）
        // false（建議）：只有命在旦夕時先閃，閃完立刻回去吃
        const bool FoodOverridesEmergency = false;

        const double Cos30 = 0.8660254;   // 60 度錐的半角
        const double Cos45 = 0.7071068;
        const double Cos60 = 0.5;

        // 以下距離都是「視野半徑的倍數」，這樣不管場地多大都能用
        const double DangerRatio = 0.55;      // 開始注意背後敵人的距離
        const double EmergencyRatio = 0.22;   // 背後敵人近到這裡 → 緊急閃避
        const double RescueRatio = 0.75;      // 離危險隊友多近才去救
        const double HuntRatio = 0.65;        // 主動狩獵的距離
        const double FoodGrabRatio = 0.5;     // 無敵時，食物在這距離內才先吃，否則去殺
        const double EdgeMarginRatio = 0.35;  // 離邊界多近開始轉回來
        const double ObstacleMarginRatio = 0.25;

        const int InvincibleSafeSteps = 8;    // 無敵剩不到幾步就當作「不無敵」，避免衝過頭

        // ---------------- 開賽前：身份宣告 ----------------
        public void Declare(RelationTable table)
        {
            // 預設：同隊友軍、異隊全部敵軍。
            // 若和同學約好互不攻擊，可在這裡寫 table.Ally("同學隊名");
            // 注意：身份只影響「我的程式」怎麼對待它，擊殺規則照舊。
        }

        // ---------------- 每一步呼叫 ----------------
        public Vector3d Decide(SelfInfo self, List<Neighbor> neighbors, List<Point3d> foods, World world)
        {
            try
            {
                if (neighbors == null) neighbors = new List<Neighbor>();
                if (foods == null) foods = new List<Point3d>();

                Vector3d result = Think(self, neighbors, foods, world);
                if (!IsFinite(result)) return Vector3d.Zero;   // 防 NaN，避免整隊被淘汰
                result.Z = 0;
                return result;
            }
            catch
            {
                // 任何意外都回傳零向量，絕不把例外丟給引擎
                return Vector3d.Zero;
            }
        }

        // ================================================================
        //  主要決策
        // ================================================================
        private Vector3d Think(SelfInfo self, List<Neighbor> neighbors, List<Point3d> foods, World world)
        {
            Point3d me = Flat(self.Position);
            Vector3d vel = Flat(self.Velocity);
            Vector3d heading = U(vel);
            double R = Math.Max(world.ViewRadius, 1e-6);

            double dangerR = R * DangerRatio;
            double emergencyR = R * EmergencyRatio;
            bool invincible = self.InvincibleSteps > InvincibleSafeSteps;

            List<Point3d> stars = self.Stars ?? new List<Point3d>();

            // 分類鄰居
            List<Neighbor> friends = new List<Neighbor>();
            List<Neighbor> others = new List<Neighbor>();   // 所有異隊（不管標成什麼，都能殺我）
            foreach (Neighbor n in neighbors)
            {
                if (n == null) continue;
                if (n.IsFriend) friends.Add(n); else others.Add(n);
            }

            // 環境力：邊界＋障礙物，永遠疊加（撞牆反彈可能讓背後露出來）
            Vector3d avoid = BoundaryForce(me, vel, world, R) + ObstacleForce(me, vel, world, R);

            // ---------- 找出對我威脅最大的敵人 ----------
            Neighbor threat = null;
            double threatScore = 0;
            foreach (Neighbor e in others)
            {
                double s = ThreatScore(Flat(e.Position), Flat(e.Velocity), me, vel, dangerR);
                if (s > threatScore) { threatScore = s; threat = e; }
            }
            double threatDist = threat != null ? Dist(me, Flat(threat.Position)) : double.MaxValue;
            bool emergency = threat != null && threatDist < emergencyR;

            Vector3d evade = threat != null ? EvadeDir(me, threat, dangerR) : Vector3d.Zero;

            Vector3d primary;

            // ================= 無敵模式：吃近的、殺能殺的 =================
            if (invincible)
            {
                Point3d food;
                bool hasFood = PickTarget(foods, me, friends, out food);
                Neighbor prey = FindPrey(me, others, R * HuntRatio * 1.3, false, self.InvincibleSteps, world);

                if (hasFood && Dist(me, food) < R * FoodGrabRatio)
                    primary = U(food - me);
                else if (prey != null)
                    primary = AttackDir(me, prey, world, R);
                else if (hasFood)
                    primary = U(food - me);
                else
                    primary = FlockDir(self, me, vel, friends, world, R);

                return Finish(primary, avoid, heading, vel, world);
            }

            // ================= 一般模式（依優先順序） =================

            // (0) 命在旦夕：敵人貼在背後
            if (emergency && !FoodOverridesEmergency)
            {
                return Finish(evade, avoid * 0.6, heading, vel, world);
            }

            // (1) 五芒星最優先
            Point3d star;
            if (PickTarget(stars, me, friends, out star))
            {
                primary = U(star - me);
                if (threat != null) primary = U(primary + evade * 0.6 * threatScore);
                return Finish(primary, avoid, heading, vel, world);
            }

            // (2) 食物
            Point3d foodPt;
            if (PickTarget(foods, me, friends, out foodPt))
            {
                primary = U(foodPt - me);
                if (threat != null) primary = U(primary + evade * 0.6 * threatScore);
                return Finish(primary, avoid, heading, vel, world);
            }

            // (3) 自己有危險 → 閃避（轉到安全角度）
            if (threat != null)
            {
                return Finish(evade, avoid, heading, vel, world);
            }

            // (4) 救隊友：找正從背後追殺隊友的敵人
            Neighbor bully = FindBully(me, friends, others, dangerR, R * RescueRatio);
            if (bully != null)
            {
                return Finish(AttackDir(me, bully, world, R), avoid, heading, vel, world);
            }

            // (5) 趁機狩獵：我已經在敵人背後
            Neighbor target = FindPrey(me, others, R * HuntRatio, true, 0, world);
            if (target != null)
            {
                return Finish(AttackDir(me, target, world, R), avoid, heading, vel, world);
            }

            // (6) 沒事做：跟隊友成群
            primary = FlockDir(self, me, vel, friends, world, R);
            return Finish(primary, avoid, heading, vel, world);
        }

        // ================================================================
        //  威脅評分：attacker 是否正從 target 背後撲上來
        //  回傳 0～1，0 表示沒威脅
        // ================================================================
        private static double ThreatScore(Point3d attacker, Vector3d attackerVel,
                                          Point3d target, Vector3d targetVel, double range)
        {
            double d = Dist(attacker, target);
            if (d > range || d < 1e-9) return 0;

            Vector3d toTarget = U(target - attacker);
            Vector3d aHead = U(attackerVel);
            if (aHead.Length < 0.5) return 0;            // 對方停著，不會從背後撞我

            // 對方的鼻子要朝向我（寬鬆用 45 度，留安全餘裕）
            double aim = Dot(aHead, toTarget);
            if (aim < Cos45) return 0;

            // 對方要在我的背後（寬鬆用 60 度半角）
            double back = 1.0;
            Vector3d tHead = U(targetVel);
            if (tHead.Length > 0.5)
            {
                back = Dot(-tHead, -toTarget);           // 我的正後方 vs 「我 → 對方」
                if (back < Cos60) return 0;
            }

            // 對方要追得上我（相對速度朝我靠近）
            double closing = Dot(attackerVel - targetVel, toTarget);
            if (closing <= 0) return 0;

            return (1.0 - d / range) * aim * (0.5 + 0.5 * back);
        }

        // ================================================================
        //  閃避方向：遠時轉頭面對敵人（正面相撞只彈開），近時側閃（側撞也只彈開）
        // ================================================================
        private static Vector3d EvadeDir(Point3d me, Neighbor e, double dangerR)
        {
            Point3d ep = Flat(e.Position);
            Vector3d toE = U(ep - me);
            Vector3d eHead = U(Flat(e.Velocity));
            if (eHead.Length < 0.5) eHead = -toE;

            // 垂直於敵人行進方向，選「離開它路徑」的那一側
            Vector3d perp = new Vector3d(-eHead.Y, eHead.X, 0);
            if (Dot(perp, me - ep) < 0) perp = -perp;

            double d = Dist(me, ep);
            double near = Clamp(1.0 - d / dangerR, 0, 1);

            Vector3d dir = toE * (1.0 - near) + perp * (0.6 + near * 1.6);
            Vector3d u = U(dir);
            return u.Length > 0.5 ? u : perp;
        }

        // ================================================================
        //  攻擊方向：從目標背後撞上去
        // ================================================================
        private static Vector3d AttackDir(Point3d me, Neighbor e, World world, double R)
        {
            Point3d ep = Flat(e.Position);
            Vector3d ev = Flat(e.Velocity);
            Vector3d eHead = U(ev);
            if (eHead.Length < 0.5) return U(ep - me);   // 對方停著：直接衝（從哪撞都算背後的機會較大）

            double d = Dist(me, ep);
            double speed = Math.Max(world.MaxSpeed, 1e-6);
            double t = Clamp(d / speed, 0, 20);
            Point3d predicted = ep + ev * t;             // 預測它未來的位置

            Vector3d rel = me - ep;
            bool iAmBehind = Dot(eHead, U(rel)) < -0.2;

            if (iAmBehind)
            {
                // 已在背後：直接追它的未來位置，撞上去時會是我的正前方對它的正後方
                return U(predicted - me);
            }

            // 我在它前面或側面：先繞到它屁股後面，避免變成正面對撞（只會彈開）
            Vector3d perp = new Vector3d(-eHead.Y, eHead.X, 0);
            if (Dot(perp, rel) < 0) perp = -perp;
            Point3d waypoint = ep - eHead * (R * 0.3) + perp * (R * 0.2);
            return U(waypoint - me);
        }

        // ================================================================
        //  找「正在背後追殺隊友」的敵人
        // ================================================================
        private static Neighbor FindBully(Point3d me, List<Neighbor> friends, List<Neighbor> others,
                                          double dangerR, double rescueRange)
        {
            Neighbor best = null;
            double bestValue = 0;
            foreach (Neighbor e in others)
            {
                if (e.InvincibleSteps > 0) continue;     // 無敵的殺不死，去也沒用
                Point3d ep = Flat(e.Position);
                double dMe = Dist(me, ep);
                if (dMe > rescueRange) continue;

                double worst = 0;
                foreach (Neighbor f in friends)
                {
                    double s = ThreatScore(ep, Flat(e.Velocity), Flat(f.Position), Flat(f.Velocity), dangerR);
                    if (s > worst) worst = s;
                }
                if (worst < 0.1) continue;

                double value = worst / (dMe + 1e-6);
                if (value > bestValue) { bestValue = value; best = e; }
            }
            return best;
        }

        // ================================================================
        //  找可以下手的獵物：我已經大致在它背後
        // ================================================================
        private static Neighbor FindPrey(Point3d me, List<Neighbor> others, double range,
                                         bool needBehind, int myInvincible, World world)
        {
            Neighbor best = null;
            double bestD = double.MaxValue;
            foreach (Neighbor e in others)
            {
                if (e.Relation != Relation.Enemy) continue;      // 只獵「敵軍」，尊重你在 Declare 的設定
                if (e.InvincibleSteps > 0) continue;
                Point3d ep = Flat(e.Position);
                double d = Dist(me, ep);
                if (d > range) continue;

                if (needBehind)
                {
                    Vector3d eHead = U(Flat(e.Velocity));
                    if (eHead.Length > 0.5 && Dot(-eHead, U(me - ep)) < Cos60) continue;
                }
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // ================================================================
        //  選食物／星星：挑最近、而且沒有隊友明顯更近且正在前往的
        //  （50 隻不要擠去搶同一顆）
        // ================================================================
        private static bool PickTarget(List<Point3d> points, Point3d me, List<Neighbor> friends, out Point3d chosen)
        {
            chosen = Point3d.Origin;
            bool found = false;
            double bestD = double.MaxValue;

            foreach (Point3d raw in points)
            {
                Point3d p = Flat(raw);
                double dMe = Dist(me, p);
                if (dMe >= bestD) continue;

                bool claimed = false;
                foreach (Neighbor f in friends)
                {
                    Point3d fp = Flat(f.Position);
                    double dF = Dist(fp, p);
                    if (dF < dMe * 0.8 && Dot(U(Flat(f.Velocity)), U(p - fp)) > Cos45)
                    {
                        claimed = true;
                        break;
                    }
                }
                if (claimed) continue;

                bestD = dMe;
                chosen = p;
                found = true;
            }
            return found;
        }

        // ================================================================
        //  閒置：聚集＋對齊，附近有隊友才能互相救援
        // ================================================================
        private static Vector3d FlockDir(SelfInfo self, Point3d me, Vector3d vel,
                                         List<Neighbor> friends, World world, double R)
        {
            Vector3d dir = Vector3d.Zero;

            if (friends.Count > 0)
            {
                double cx = 0, cy = 0;
                Vector3d avgVel = Vector3d.Zero;
                foreach (Neighbor f in friends)
                {
                    cx += f.Position.X; cy += f.Position.Y;
                    avgVel += Flat(f.Velocity);
                }
                Point3d centroid = new Point3d(cx / friends.Count, cy / friends.Count, 0);

                double cohesionW = world.Phase == 3 ? 1.4 : 0.8;   // 決戰期抱得更緊
                dir += U(centroid - me) * cohesionW;
                dir += U(avgVel) * 1.0;
            }
            else
            {
                // 落單：往場中央靠，比較容易遇到食物與隊友
                dir += U(Flat(world.Center) - me) * 0.6;
            }

            // 少量隨機遊走（一定要用 self.Rng，才能重播）
            double a = self.Rng.NextDouble() * Math.PI * 2.0;
            dir += new Vector3d(Math.Cos(a), Math.Sin(a), 0) * 0.25;

            Vector3d u = U(dir);
            return u.Length > 0.5 ? u : U(vel);
        }

        // ================================================================
        //  邊界：預測幾步後的位置，太靠邊就往內推；縮小期留更多空間
        // ================================================================
        private static Vector3d BoundaryForce(Point3d me, Vector3d vel, World world, double R)
        {
            Point3d c = Flat(world.Center);
            double h = world.HalfSize;
            double margin = R * EdgeMarginRatio * (world.Phase == 2 ? 1.8 : 1.0);
            margin = Math.Min(margin, h * 0.5);
            if (margin < 1e-6) return Vector3d.Zero;

            Point3d p = me + vel * 6.0;
            double fx = 0, fy = 0;
            double inner = h - margin;

            double dx = p.X - c.X, dy = p.Y - c.Y;
            if (dx > inner) fx -= (dx - inner) / margin;
            if (dx < -inner) fx += (-inner - dx) / margin;
            if (dy > inner) fy -= (dy - inner) / margin;
            if (dy < -inner) fy += (-inner - dy) / margin;

            Vector3d f = new Vector3d(fx, fy, 0) * 2.5;

            // 縮小期：持續輕輕往中心靠
            if (world.Phase == 2) f += U(c - me) * 0.3;
            return f;
        }

        // ================================================================
        //  障礙物：現在位置與前方預測點都檢查，靠太近就推開
        // ================================================================
        private static Vector3d ObstacleForce(Point3d me, Vector3d vel, World world, double R)
        {
            Vector3d f = Vector3d.Zero;
            if (world.Obstacles == null) return f;

            double margin = R * ObstacleMarginRatio;
            Point3d ahead = me + vel * 5.0;

            foreach (var ob in world.Obstacles)
            {
                if (ob == null) continue;
                f += PushFrom(ob.DistanceTo(me), ob.ClosestPoint(me), Flat(ob.Center), me, margin) * 2.0;
                f += PushFrom(ob.DistanceTo(ahead), ob.ClosestPoint(ahead), Flat(ob.Center), ahead, margin * 0.7) * 1.2;
            }
            return f;
        }

        private static Vector3d PushFrom(double d, Point3d closest, Point3d center, Point3d p, double margin)
        {
            if (margin < 1e-6 || d >= margin) return Vector3d.Zero;
            Vector3d away = p - Flat(closest);
            if (d <= 0 || away.Length < 1e-6) away = p - center;   // 在裡面或剛好貼邊
            return U(away) * ((margin - Math.Max(d, 0)) / margin);
        }

        // ================================================================
        //  把方向與環境力合在一起，交給 Steering.Seek
        // ================================================================
        private static Vector3d Finish(Vector3d primary, Vector3d avoid, Vector3d heading, Vector3d vel, World world)
        {
            Vector3d desired = U(primary) + avoid;
            Vector3d u = U(desired);
            if (u.Length < 0.5) u = heading.Length > 0.5 ? heading : new Vector3d(1, 0, 0);
            Vector3d force = Steering.Seek(u, vel, world);
            force.Z = 0;
            return force;
        }

        // ================================================================
        //  小工具（全部 2D）
        // ================================================================
        private static Point3d Flat(Point3d p) { return new Point3d(p.X, p.Y, 0); }
        private static Vector3d Flat(Vector3d v) { return new Vector3d(v.X, v.Y, 0); }

        private static Vector3d U(Vector3d v)
        {
            v.Z = 0;
            if (!IsFinite(v) || v.Length < 1e-9) return Vector3d.Zero;
            return Steering.Unit(v);
        }

        private static double Dot(Vector3d a, Vector3d b) { return a.X * b.X + a.Y * b.Y; }

        private static double Dist(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Clamp(double x, double lo, double hi) { return x < lo ? lo : (x > hi ? hi : x); }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) ||
                     double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
        }
    }
}
