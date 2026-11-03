using System.Collections.Generic;
using Rhino.Geometry;

namespace BoidArena
{
    public class Boid_拾星者 : IBrain, IDeclareRelations
    {
        public string Name { get { return "拾星者"; } }

        public void Declare(RelationTable table)
        {
            // 保留預設身份；異隊即使被標成友軍，碰撞仍可能致命。
        }

        public Vector3d Decide(SelfInfo self, List<Neighbor> neighbors,
            List<Point3d> foods, World world)
        {
            // 不保存個體狀態，因此同一個腦袋供全隊共用也不會互相干擾。
            if (world == null || !self.Position.IsValid || !self.Velocity.IsValid)
                return Vector3d.Zero;
            if (!world.Center.IsValid || !(world.HalfSize > 0) ||
                !(world.MaxSpeed > 0) || !(world.MaxForce > 0))
                return Vector3d.Zero;

            Vector3d velocity = Flat(self.Velocity);
            Vector3d heading = Steering.Unit(velocity);
            double speed = velocity.Length;
            double view = System.Math.Max(world.ViewRadius, world.MaxSpeed * 4);
            double fearRadius = System.Math.Max(view * 0.45, world.MaxSpeed * 5);
            double turnSteps = world.MaxSpeed / world.MaxForce;
            double horizon = Clamp(turnSteps, 3, 12);
            // 無敵快結束時提早恢復避敵，留出轉向撤退的時間。
            bool shield = self.InvincibleSteps > turnSteps + 6;
            Vector3d flee = Vector3d.Zero;
            double danger = 0;
            bool hasThreat = false;
            Point3d threat = self.Position;
            double nearestThreat = double.MaxValue;
            bool hasPrey = false;
            Point3d prey = self.Position;
            double bestPrey = double.MaxValue;

            // 每隻鄰居只掃一次；同隊分離完全交給引擎。
            if (neighbors != null)
            {
                for (int i = 0; i < neighbors.Count; i++)
                {
                    Neighbor n = neighbors[i];
                    if (n.Team == self.Team || n.IsFriend ||
                        !n.Position.IsValid || !n.Velocity.IsValid) continue;

                    Vector3d delta = Flat(n.Position - self.Position);
                    double distance = delta.Length;
                    Vector3d toward = Steering.Unit(delta);
                    Vector3d enemyHeading = Steering.Unit(Flat(n.Velocity));
                    // 六十度錐的總角度若為六十度，半角就是三十度。
                    // 採用更窄的判斷，保留轉向與碰撞的誤差空間。
                    bool behindEnemy = Dot(enemyHeading, toward) > 0.90;
                    bool aligned = Dot(heading, toward) > 0.90;
                    bool tailOpportunity = behindEnemy && aligned;

                    if (distance < nearestThreat)
                    {
                        nearestThreat = distance;
                        threat = n.Position;
                        hasThreat = true;
                    }

                    // 無敵不代表正面能殺人；仍以敵人的後方作為接近點。
                    // 預留轉向與撤退時間；對方無敵時絕不列為獵物。
                    double travelSteps = distance / world.MaxSpeed;
                    bool shieldEnough = self.InvincibleSteps > travelSteps + turnSteps + 12;
                    if (n.Relation == Relation.Enemy && n.InvincibleSteps <= 0 &&
                        enemyHeading.SquareLength > 0 &&
                        (shieldEnough || (!shield && tailOpportunity && distance < fearRadius)))
                    {
                        double score = distance + (behindEnemy ? 0 : view);
                        if (score < bestPrey)
                        {
                            bestPrey = score;
                            double lead = System.Math.Min(travelSteps * 0.35, 3);
                            double rearOffset = tailOpportunity ? 0 : world.MaxSpeed * 3;
                            prey = n.Position + Flat(n.Velocity) * lead - enemyHeading * rearOffset;
                            hasPrey = true;
                        }
                    }

                    if (shield) continue;

                    // 預測短時間內的最近接近點，提早避開迎面交會。
                    Vector3d relative = Flat(n.Velocity) - velocity;
                    double relativeSpeed2 = relative.SquareLength;
                    double time = relativeSpeed2 > 1e-12
                        ? Clamp(-Dot(delta, relative) / relativeSpeed2, 0, horizon) : 0;
                    Vector3d closest = delta + relative * time;
                    double closeDistance = closest.Length;
                    double risk = Clamp(1 - distance / fearRadius, 0, 1);
                    if (time > 0 && closeDistance < world.MaxSpeed * 3)
                        risk = System.Math.Max(risk,
                            (1 - closeDistance / (world.MaxSpeed * 3)) / (1 + time * 0.12));

                    // 正後方有人追殺時加入橫移，避免只沿直線逃跑。
                    bool rearThreat = Dot(enemyHeading, -toward) > 0.80 &&
                        Dot(heading, -toward) > 0.80;
                    if (rearThreat && distance < fearRadius) risk *= 1.6;
                    if (n.InvincibleSteps > 0) risk *= 1.5;
                    if (tailOpportunity && n.InvincibleSteps <= 0) risk *= 0.15;
                    Vector3d away = -Steering.Unit(closest);
                    if (away.SquareLength < 1e-12) away = -toward;
                    if (away.SquareLength < 1e-12)
                        away = new Vector3d(self.Team % 2 == 0 ? 1 : -1, 0, 0);
                    if (rearThreat)
                    {
                        Vector3d side = new Vector3d(-heading.Y, heading.X, 0);
                        if (Dot(side, away) < 0) side = -side;
                        away = Steering.Unit(away + side * 1.4);
                    }
                    flee += away * risk;
                    danger = System.Math.Max(danger, risk);
                }
            }

            // 第三階段只是停止生成食物；仍然吃場上剩餘的食物。
            Point3d star, food;
            bool hasStar = Resource(self.Stars, self, world, threat,
                hasThreat && !shield, fearRadius, out star);
            bool hasFood = Resource(foods, self, world, threat,
                hasThreat && !shield, fearRadius, out food);

            Vector3d desired;
            if (hasStar && self.InvincibleSteps < 20)
                desired = Flat(star - self.Position);
            else if (shield && hasPrey)
                desired = Flat(prey - self.Position);
            else if (hasFood)
                desired = Flat(food - self.Position);
            else if (hasStar)
                desired = Flat(star - self.Position);
            else if (hasPrey && danger < 0.25)
                desired = Flat(prey - self.Position);
            else
            {
                // 食物吃完且沒有安全的背襲機會時，繼續探索。
                // 亂數僅由引擎提供，重播時可以得到相同結果。
                double r = self.Rng == null ? 0.5 : self.Rng.NextDouble();
                double angle = heading.SquareLength > 0
                    ? System.Math.Atan2(heading.Y, heading.X) + (r - 0.5) * 0.35
                    : r * System.Math.PI * 2;
                desired = new Vector3d(System.Math.Cos(angle), System.Math.Sin(angle), 0);
            }

            desired = Steering.Unit(desired);
            if (flee.SquareLength > 1e-12)
            {
                // 危險時求生優先；危險較低時仍保留覓食的方向。
                desired = danger > 0.55 ? Steering.Unit(flee)
                    : Steering.Unit(desired + Steering.Unit(flee) * danger * 2);
            }
            if (desired.SquareLength < 1e-12)
                desired = heading.SquareLength > 0 ? heading : Vector3d.XAxis;

            // 邊界留出轉向空間；縮圈期額外提前往場內靠。
            double margin = world.MaxSpeed * 3 + speed * speed / (2 * world.MaxForce);
            if (world.Phase == 2) margin *= 1.35;
            margin = System.Math.Min(margin, world.HalfSize * 0.45);
            double px = self.Position.X - world.Center.X;
            double py = self.Position.Y - world.Center.Y;
            double safe = world.HalfSize - margin;
            Vector3d wall = new Vector3d(
                WallAxis(px, velocity.X, safe, margin, horizon),
                WallAxis(py, velocity.Y, safe, margin, horizon), 0);
            if (wall.SquareLength > 1e-12)
                desired = Steering.Unit(desired + wall * 3);

            // 路徑穿過矩形時繞行；已在障礙物內則走最近的出口。
            desired = AvoidRectangles(self.Position, velocity, desired, world, horizon);
            Vector3d force = Flat(Steering.Seek(desired, velocity, world));
            if (!force.IsValid) return Vector3d.Zero;
            if (force.Length > world.MaxForce)
                force = Steering.Unit(force) * world.MaxForce;
            return force;
        }

        private bool Resource(List<Point3d> points, SelfInfo self, World world,
            Point3d threat, bool avoidThreat, double radius, out Point3d result)
        {
            result = self.Position;
            if (points == null) return false;
            double best = double.MaxValue;
            bool found = false;
            for (int i = 0; i < points.Count; i++)
            {
                Point3d p = points[i];
                if (!p.IsValid || System.Math.Abs(p.X - world.Center.X) > world.HalfSize ||
                    System.Math.Abs(p.Y - world.Center.Y) > world.HalfSize) continue;
                double score = Flat(p - self.Position).Length;
                // 用最近的異隊估計資源風險，避免逐一配對造成大量計算。
                if (avoidThreat)
                    score += System.Math.Max(0, radius - Flat(p - threat).Length) * 3;
                if (score >= best) continue;
                best = score;
                result = p;
                found = true;
            }
            return found;
        }

        private Vector3d AvoidRectangles(Point3d position, Vector3d velocity,
            Vector3d desired, World world, double horizon)
        {
            if (world.Obstacles == null) return desired;
            double padding = world.MaxSpeed * 1.5;
            Vector3d path = velocity * (horizon * 0.5) +
                desired * (world.MaxSpeed * horizon * 0.75);
            double first = double.MaxValue;
            Vector3d avoidance = desired;
            for (int i = 0; i < world.Obstacles.Count; i++)
            {
                var ob = world.Obstacles[i];
                if (ob == null) continue;
                double x = position.X - ob.Center.X;
                double y = position.Y - ob.Center.Y;
                bool inside = System.Math.Abs(x) <= ob.HalfWidth &&
                    System.Math.Abs(y) <= ob.HalfHeight;
                Vector3d normal;
                double entry = 0;
                if (inside)
                {
                    // 最近點在矩形內會等於自己，所以不能直接相減求逃生方向。
                    double dx = ob.HalfWidth - System.Math.Abs(x);
                    double dy = ob.HalfHeight - System.Math.Abs(y);
                    normal = dx <= dy ? new Vector3d(x >= 0 ? 1 : -1, 0, 0)
                        : new Vector3d(0, y >= 0 ? 1 : -1, 0);
                    entry = -1;
                }
                else
                {
                    double exit = 1;
                    if (!Slab(x, path.X, ob.HalfWidth + padding, ref entry, ref exit) ||
                        !Slab(y, path.Y, ob.HalfHeight + padding, ref entry, ref exit)) continue;
                    Point3d hit = position + path * entry;
                    normal = Steering.Unit(Flat(hit - ob.ClosestPoint(hit)));
                    if (normal.SquareLength < 1e-12)
                        normal = Steering.Unit(Flat(position - ob.ClosestPoint(position)));
                }
                if (entry >= first) continue;
                first = entry;
                if (inside) avoidance = normal;
                else
                {
                    Vector3d tangent = new Vector3d(-normal.Y, normal.X, 0);
                    if (Dot(tangent, desired) < 0) tangent = -tangent;
                    double push = entry < 0.2 ? 1.5 : 0.5;
                    avoidance = Steering.Unit(normal * push + tangent * 1.5);
                }
            }
            return avoidance;
        }

        private bool Slab(double position, double move, double half,
            ref double entry, ref double exit)
        {
            // 線段與矩形的單軸區間測試；平行於邊時不做除法。
            if (System.Math.Abs(move) < 1e-12)
                return System.Math.Abs(position) <= half;
            double a = (-half - position) / move;
            double b = (half - position) / move;
            entry = System.Math.Max(entry, System.Math.Min(a, b));
            exit = System.Math.Min(exit, System.Math.Max(a, b));
            return entry <= exit;
        }

        private double WallAxis(double position, double velocity,
            double safe, double margin, double horizon)
        {
            // 比較目前位置與預測位置，避免尚未越界就繼續朝外衝。
            double future = position + velocity * horizon * 0.7;
            double upper = System.Math.Max(position, future) - safe;
            double lower = -safe - System.Math.Min(position, future);
            double scale = System.Math.Max(margin, 1e-9);
            return Clamp(lower / scale, 0, 2) - Clamp(upper / scale, 0, 2);
        }

        private Vector3d Flat(Vector3d v)
        {
            return new Vector3d(v.X, v.Y, 0);
        }

        private double Dot(Vector3d a, Vector3d b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        private double Clamp(double value, double low, double high)
        {
            return System.Math.Max(low, System.Math.Min(high, value));
        }
    }
}
