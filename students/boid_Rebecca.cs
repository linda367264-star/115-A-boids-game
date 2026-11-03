using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace BoidArena
{
    // ============================================================================
    // 學生範本：複製這個檔，改檔名成 boid_{你的名字}.cs，
    // 並把 class 名稱改成 Boid_{你的名字}、Name 改成你的隊名（只改這個檔，不要動 core、host）
    //
    // 這個檔只列出「你拿得到什麼」，怎麼用、要寫什麼行為，全部由你自己決定。
    // 所有資料都是唯讀的複本，改它不會影響別人，也不會影響比賽。
    // ============================================================================
    //
    // 【每隻鳥每一步，引擎呼叫一次 Decide，傳進 4 組資料】
    //
    // ■ self（SelfInfo）：這一隻自己
    //     self.Position          Point3d    我的位置
    //     self.Velocity          Vector3d   我的速度（方向＝我面向哪裡）
    //     self.Team              int        我的隊伍編號
    //     self.Name              string     我的隊名（等於下面 Name 屬性的值）
    //     self.InvincibleSteps   int        我還剩幾步無敵（0 = 沒有）
    //     self.Stars             List<Point3d>  視野內的五芒星位置（沒有就是空清單）
    //     self.Rng               Random     我的專屬亂數產生器（要亂數只能用它）
    //
    // ■ neighbors（List<Neighbor>）：視野內的其他鳥（不含自己；沒有就是空清單）。每一個 Neighbor 有：
    //     Position           Point3d    它的位置
    //     Velocity           Vector3d   它的速度
    //     Team               int        它的隊伍編號
    //     Name               string     它所屬隊伍的隊名
    //     Relation           Relation   對我來說它是什麼身份：Ally 友軍／Enemy 敵軍／Neutral 不相關
    //                                   （Self 這個值是留給 table.Get 查自己用的）
    //                                   預設：同隊＝友軍，異隊＝敵軍；可以在 Declare 裡改
    //     IsFriend           bool       是不是同隊（不受 Declare 影響）
    //     InvincibleSteps    int        它還剩幾步無敵（0 = 沒有）
    //     Distance           double     它離我多遠
    //
    // ■ foods（List<Point3d>）：視野內的食物位置（沒有就是空清單）
    //
    // ■ world（World）：場地與物理限制，全隊相同
    //     world.Center       Point3d    場地中心（原點）
    //     world.HalfSize     double     場地目前的半邊長（正方形；縮小期會變小）
    //     world.Step         int        現在是第幾步
    //     world.Phase        int        現在的階段：1 穩定期、2 縮小期、3 決戰期
    //     world.ViewRadius   double     視野半徑（10）
    //     world.MaxSpeed     double     速度上限（0.5）
    //     world.MaxForce     double     轉向力上限（0.05）
    //     world.Obstacles    IList<Obstacle>  目前場上的障礙物（矩形，與座標軸平行）。每個 Obstacle 有：
    //         Center         Point3d    中心
    //         HalfWidth      double     X 方向的半寬
    //         HalfHeight     double     Y 方向的半高
    //         ClosestPoint(Point3d p)   Point3d  矩形上離 p 最近的點
    //         DistanceTo(Point3d p)     double   p 到矩形邊緣的距離（在裡面是 0）
    //
    // 【可以呼叫的函式】
    //     Steering.Unit(Vector3d v)                          回傳 v 的單位向量（太短就回零向量）
    //     Steering.Seek(Vector3d 想去的方向, Vector3d 目前速度, World world)
    //                                                        回傳「往那個方向轉」的轉向力（長度已限制在 MaxForce）
    //
    // 【開賽前只呼叫一次的 Declare(RelationTable table)，table 有：】
    //     table.MyName        string          我的隊名
    //     table.AllNames      IList<string>   這一場所有參賽隊伍的名字（含自己）
    //     table.Ally(string 隊名)               把某隊標成友軍
    //     table.Enemy(string 隊名)              把某隊標成敵軍
    //     table.Neutral(string 隊名)            把某隊標成不相關
    //     table.Get(string 隊名)  Relation       查某隊現在的身份（自己這隊回傳 Self）
    //   不需要的話，可以把 `: IBrain, IDeclareRelations` 改成 `: IBrain`，並刪掉 Declare。
    //
    // 【回傳值】
    //     Decide 回傳一個 Vector3d，代表「我想往哪轉」。引擎會自動把它限制在 MaxForce 以內，
    //     而且這是 2D 遊戲：Z 方向一律被丟掉。
    //
    // 【遊戲規則裡和你有關的事實】
    //     - 全場 2D，場地是以原點為中心的正方形；一回合約 3 分鐘，存活最多的隊伍獲勝
    //     - 吃到食物：該隻複製成兩隻（全場上限 500 隻，滿了不能出生）
    //     - 吃到五芒星：無敵 100 步，殺不死，但照樣能殺別人
    //     - 異隊碰到時：A 的前方 60 度碰到 B 的正後方 60 度，A 殺死 B；正面對撞或側邊碰撞互相彈開
    //     - 身份（友軍／敵軍／不相關）只是你自己用的標籤，不改變擊殺規則
    //     - 每隊每步所有 Decide 加起來要在 15 毫秒內；丟例外、回傳 NaN 或太慢，整隊淘汰
    //     - 顏色由老師分配，你不用寫
    //
    // 【禁止】（違反的檔案不會參賽）
    //     不可使用檔案、網路、執行緒、反射；不可自己 new Random、不可用 DateTime.Now、Guid.NewGuid、Environment；
    //     不可有 static 可變欄位（static readonly 可以）；不可碰 Arena、Agent、Roster；
    //     不要用 Vector3d.Unitize（改用 Steering.Unit）；需要額外的輔助類別，名稱前面加上你的名字
    // ============================================================================

    // 性格：均衡型。優先順序大致是「不撞牆/障礙 > 躲背後的敵人 > 搶五芒星 > 吃食物 > 追殺落單敵人 > 跟著同伴」
    public class Boid_balanced : IBrain, IDeclareRelations
    {
        public string Name { get { return "均衡隊"; } }

        // 開賽前只呼叫一次：用預設身份（同隊友軍、異隊敵軍），不需要額外設定
        public void Declare(RelationTable table)
        {
        }

        // 每隻鳥每一步都會呼叫一次，回傳「想往哪轉」的向量
        public Vector3d Decide(SelfInfo self, List<Neighbor> neighbors, List<Point3d> foods, World world)
        {
            Point3d p = self.Position;
            bool inv = self.InvincibleSteps > 0;

            // 我面向的方向（只看 XY）；速度太小時隨機選一個方向
            Vector3d h = Steering.Unit(new Vector3d(self.Velocity.X, self.Velocity.Y, 0));
            if (h.Length < 1e-6)
            {
                double a = self.Rng.NextDouble() * 2.0 * Math.PI;
                h = new Vector3d(Math.Cos(a), Math.Sin(a), 0);
            }

            double view = world.ViewRadius;

            // ---------- 1. 牆與障礙物：保持距離 ----------
            Vector3d avoid = Vector3d.Zero;

            double margin = 5.0;
            double lim = world.HalfSize - margin;
            double lx = p.X - world.Center.X;
            double ly = p.Y - world.Center.Y;
            if (lx > lim) avoid += new Vector3d(-(lx - lim) / margin, 0, 0);
            if (lx < -lim) avoid += new Vector3d(-(lx + lim) / margin, 0, 0);
            if (ly > lim) avoid += new Vector3d(0, -(ly - lim) / margin, 0);
            if (ly < -lim) avoid += new Vector3d(0, -(ly + lim) / margin, 0);

            double safe = 4.0;
            Point3d ahead = new Point3d(p.X + h.X * 3.0, p.Y + h.Y * 3.0, 0);
            foreach (Obstacle o in world.Obstacles)
            {
                // 同時檢查現在的位置與前方 3 單位的位置
                Point3d[] probes = new Point3d[] { p, ahead };
                foreach (Point3d q in probes)
                {
                    double d = o.DistanceTo(q);
                    if (d >= safe) continue;
                    Point3d cp = o.ClosestPoint(q);
                    Vector3d away = new Vector3d(q.X - cp.X, q.Y - cp.Y, 0);
                    if (d <= 1e-6) away = new Vector3d(q.X - o.Center.X, q.Y - o.Center.Y, 0); // 在裡面：往中心外推
                    avoid += Steering.Unit(away) * ((safe - d) / safe);
                }
            }

            // ---------- 2. 鄰居：敵人（逃／追）與友軍（群聚） ----------
            Vector3d flee = Vector3d.Zero;
            Vector3d chase = Vector3d.Zero;
            Vector3d cohesion = Vector3d.Zero;
            Vector3d align = Vector3d.Zero;
            int allyCount = 0;
            double cosCone = 0.77;   // 約 40 度，比引擎的 30 度半角寬一點，留安全邊際

            foreach (Neighbor n in neighbors)
            {
                if (n.IsFriend)
                {
                    cohesion += new Vector3d(n.Position.X - p.X, n.Position.Y - p.Y, 0);
                    align += Steering.Unit(new Vector3d(n.Velocity.X, n.Velocity.Y, 0));
                    allyCount++;
                    continue;
                }

                Vector3d d = new Vector3d(n.Position.X - p.X, n.Position.Y - p.Y, 0);
                double dist = d.Length;
                if (dist < 1e-6) continue;
                Vector3d u = d / dist;                                               // 從我指向它
                Vector3d eh = Steering.Unit(new Vector3d(n.Velocity.X, n.Velocity.Y, 0)); // 它面向的方向

                // 危險：它在我背後，而且正面朝著我 → 逃（無敵時不用逃）
                if (!inv)
                {
                    bool behindMe = Vector3d.Multiply(h, u) <= -cosCone;
                    bool facingMe = Vector3d.Multiply(eh, u) <= -cosCone;
                    if (behindMe && facingMe)
                    {
                        flee += (-u) * ((view - dist) / view + 0.3);
                    }
                    else if (dist < 3.0)
                    {
                        // 太近的異隊鳥：稍微拉開距離，避免被側擊或卡位
                        flee += (-u) * ((3.0 - dist) / 3.0) * 0.5;
                    }
                }

                // 機會：它是敵軍、不是無敵、我在它背後 → 追
                if (n.Relation == Relation.Enemy && n.InvincibleSteps == 0)
                {
                    bool iAmBehind = Vector3d.Multiply(eh, u) >= 0.5;   // 它背對著我
                    bool facingIt = Vector3d.Multiply(h, u) >= 0.5;     // 我朝著它
                    if (inv)
                        chase += u * (1.0 / (1.0 + dist * 0.2));        // 無敵時：直接找最近的敵人撞
                    else if (iAmBehind && facingIt)
                        chase += u * (1.0 / (1.0 + dist * 0.2));
                }
            }

            if (allyCount > 0)
            {
                cohesion = Steering.Unit(cohesion / allyCount);
                align = Steering.Unit(align);
            }

            // ---------- 3. 最近的食物與五芒星 ----------
            Vector3d foodDir = Vector3d.Zero;
            double best = double.MaxValue;
            foreach (Point3d f in foods)
            {
                double d = Math.Sqrt((f.X - p.X) * (f.X - p.X) + (f.Y - p.Y) * (f.Y - p.Y));
                if (d < best)
                {
                    best = d;
                    foodDir = new Vector3d(f.X - p.X, f.Y - p.Y, 0);
                }
            }
            foodDir = Steering.Unit(foodDir);

            Vector3d starDir = Vector3d.Zero;
            best = double.MaxValue;
            foreach (Point3d s in self.Stars)
            {
                double d = Math.Sqrt((s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y));
                if (d < best)
                {
                    best = d;
                    starDir = new Vector3d(s.X - p.X, s.Y - p.Y, 0);
                }
            }
            starDir = Steering.Unit(starDir);

            // ---------- 4. 加權合成 ----------
            double foodWeight = flee.Length > 0.5 ? 0.3 : 1.2;   // 被追的時候先保命，不要硬去吃
            double aggression = world.Phase == 3 ? 2.0 : 1.5;    // 決戰期更積極獵殺

            Vector3d desired = avoid * 3.0
                             + flee * 3.0
                             + starDir * 2.0
                             + foodDir * foodWeight
                             + chase * aggression
                             + cohesion * 0.3
                             + align * 0.3;

            // 什麼都沒有時：沿著原方向，加一點亂數小幅轉彎（用 self.Rng 才能重播）
            if (desired.Length < 1e-6)
            {
                double jitter = (self.Rng.NextDouble() - 0.5) * 0.6;
                desired = new Vector3d(
                    h.X * Math.Cos(jitter) - h.Y * Math.Sin(jitter),
                    h.X * Math.Sin(jitter) + h.Y * Math.Cos(jitter), 0);
            }

            desired = new Vector3d(desired.X, desired.Y, 0);
            Vector3d force = Steering.Seek(desired, self.Velocity, world);

            // 保險：避免 NaN 讓整隊被淘汰
            if (double.IsNaN(force.X) || double.IsNaN(force.Y) ||
                double.IsInfinity(force.X) || double.IsInfinity(force.Y))
                return Vector3d.Zero;

            return new Vector3d(force.X, force.Y, 0);
        }
    }
}