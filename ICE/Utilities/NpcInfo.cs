using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Numerics; // Add this for Vector2 and Vector3

namespace ICE.Utilities;

internal static class NpcData // Renamed the class to avoid conflict
{
    public enum NpcType
    {
        Repair,
        Credit,
        Relic,
        Gamba,
        Drone,
        RedAlert
    }

    public class NPCInfo // Keep this class for the dictionary
    {
        public uint NpcId { get; set; }
        public string Name { get; set; }
        public Vector3 Location_Npc { get; set; }
        public Vector3 Location_Circle { get; set; }
    }

    public static Dictionary<uint, Dictionary<NpcType, NPCInfo>> MoonNpcs = new()
    {
        [1237] = new Dictionary<NpcType, NPCInfo>
        {
            [NpcType.Repair] = new NPCInfo // Repair | Gil Gear Vendor
            {
                NpcId = 1052610,
                Name = "Godgyth",
                Location_Npc = new Vector3(19.46f, 1.69f, 18.11f),
                Location_Circle = new Vector3(16.97f, 1.69f, 15.16f),
            },
            [NpcType.Credit] = new NPCInfo // Credit Exchange Vendor
            {
                NpcId = 1052608,
                Name = "Mesouaidonque",
                Location_Npc = new Vector3(18.23f, 1.69f, 19.42f),
                Location_Circle = new Vector3(15.08f, 1.69f, 16.89f),
            },
            [NpcType.Relic] = new NPCInfo // Relic NPC
            {
                NpcId = 1052605,
                Name = "Researchingway",
                Location_Npc = new Vector3(-18.91f, 2.15f, 18.84f),
                Location_Circle = new Vector3(-16.03f, 1.69f, 16.10f),
            },
            [NpcType.Gamba] = new NPCInfo // Cosmic Fortune aka Gamba Wheel
            {
                NpcId = 1052612,
                Name = "Orbitingway",
                Location_Npc = new Vector3(18.84f, 2.24f, -18.91f),
                Location_Circle = new Vector3(15.92f, 1.69f, -16.16f),
            },
            [NpcType.RedAlert] = new()
            {
                NpcId = 1052663,
                Name = "Lefleda",
                Location_Npc = new(16.74f, 1.71f, -3.86f),
                Location_Circle = new(15.28f, 1.64f, -3.83f),

            }
        },
        [1291] = new Dictionary<NpcType, NPCInfo>
        {
            [NpcType.Repair] = new NPCInfo
            {
                NpcId = 1052641,
                Name = "Godgyth",
                Location_Npc = new Vector3(359.52f, 52.75f, -401.72f),
                Location_Circle = new(357.25f, 52.69f, -405.16f),
            },
            [NpcType.Credit] = new NPCInfo // Credit Exchange Vendor
            {
                NpcId = 1052640,
                Name = "Mesouaidonque",
                Location_Npc = new Vector3(358.33f, 52.75f, -400.44f),
                Location_Circle = new(354.94f, 52.69f, -402.99f),
            },
            [NpcType.Relic] = new NPCInfo // Relic NPC
            {
                NpcId = 1052629,
                Name = "Researchingway",
                Location_Npc = new Vector3(321.22f, 53.19f, -401.24f),
                Location_Circle = new(323.92f, 52.69f, -404.05f),
            },
            [NpcType.Gamba] = new NPCInfo // Cosmic Fortune aka Gamba Wheel
            {
                NpcId = 1052642,
                Name = "Orbitingway",
                Location_Npc = new Vector3(358.82f, 53.19f, -438.86f),
                Location_Circle = new Vector3(355.97f, 52.69f, -436.08f),
            },
            [NpcType.RedAlert] = new NPCInfo()
            {
                NpcId = 1052626,
                Name = "Lefleda",
                Location_Npc = new(343.89f, 52.64f, -443.47f),
                Location_Circle = new(343.46f, 52.64f, -441.80f),
            }
        },
        [1310] = new Dictionary<NpcType, NPCInfo>
        {
            [NpcType.Repair] = new NPCInfo // Repair | Gil Gear Vendor
            {
                NpcId = 1052651,
                Name = "Godgyth",
                Location_Npc = new Vector3(-202.44f, 0.65f, 154.31f),
                Location_Circle = new Vector3(-198.98f, 0.50f, 153.69f),
            },
            [NpcType.Credit] = new NPCInfo // Credit Exchange Vendor
            {
                NpcId = 1052650,
                Name = "Mesouaidonque",
                Location_Npc = new Vector3(-202.20f, 0.65f, 152.54f),
                Location_Circle = new Vector3(-198.98f, 0.50f, 153.69f),
            },
            [NpcType.Relic] = new NPCInfo // Relic NPC
            {
                NpcId = 1052647,
                Name = "Researchingway",
                Location_Npc = new Vector3(-202.26f, 1.19f, 122.00f),
                Location_Circle = new Vector3(-199.07f, 0.50f, 121.97f),
            },
            [NpcType.Gamba] = new NPCInfo // Cosmic Fortune aka Gamba Wheel
            {
                NpcId = 1052652,
                Name = "Orbitingway",
                Location_Npc = new Vector3(-157.73f, 1.19f, 153.98f),
                Location_Circle = new Vector3(-161.04f, 0.50f, 153.84f),
            },
            [NpcType.Drone] = new NPCInfo
            {
                NpcId = 1052654,
                Name = "Kaede",
                Location_Npc = new Vector3(-206.38f, 0.50f, 131.09f),
                Location_Circle = new(-205.85f, 0.65f, 134.19f),
            },
            [NpcType.RedAlert] = new NPCInfo()
            {
                NpcId = 1052645,
                Name = "Lefleda",
                Location_Circle = new(-156.91f, 0.50f, 143.07f),
                Location_Npc = new(-155.02f, 0.50f, 144.58f),
            }
        },
        [1319] = new Dictionary<NpcType, NPCInfo>  // Auxesia (2026-06-02 NpcId確定・座標要実機取得)
        {
            [NpcType.Repair] = new NPCInfo
            {
                // Godgythはメズエードンクに隣接。立ち位置はMesouaidonqueと同じ(314.88,376.06)で会話可(ユーザー確認)。
                // CircleとNpcを分離してNPC衝突→走り続けを回避(distance:5判定 約1.6m<5)。
                NpcId = 1056825,
                Name = "Godgyth",
                Location_Npc = new Vector3(317.68f, 205.75f, 374.78f), // 2026-06-04 再取得(開拓で移動)
                Location_Circle = new Vector3(314.88f, 205.64f, 376.06f),
            },
            [NpcType.Credit] = new NPCInfo
            {
                // NPC実位置(317.74,205.75,376.70) / 話しかける立ち位置(314.88,205.64,376.06)=約2.9m手前(ユーザー実機確定)。
                // CircleとNpcを分離してNPC衝突→走り続けを回避(distance:6判定 約2.9m<6)。
                NpcId = 1056824,
                Name = "Mesouaidonque",
                Location_Npc = new Vector3(317.74f, 205.75f, 376.70f),
                Location_Circle = new Vector3(314.88f, 205.64f, 376.06f),
            },
            [NpcType.Relic] = new NPCInfo  // ★Relic Turnin対象
            {
                // NPC位置: (291.25, 205.75, 400.66)。CircleをNPCと同座標にするとNPCに衝突して到達できず、
                // スタック→ジャンプ/再走行を延々繰り返し公衆の面前で走り続ける危険があった(実機報告)。
                // ハブ側(低Z)へ約4m手前に停止位置を設定(distance:5判定OK 4.16m<5 / 会話可能距離 / 衝突回避)。
                NpcId = 1056821,
                Name = "Researchingway",
                Location_Npc = new Vector3(291.00f, 206.21f, 402.58f),    // 2026-06-04 再取得(開拓で移動)
                Location_Circle = new Vector3(291.45f, 205.64f, 399.14f), // 実機で立って会話できた位置(NPCまで約3.5m)
            },
            [NpcType.Gamba] = new NPCInfo
            {
                // NPC実位置(290.94,206.21,349.36) / 話しかける立ち位置(291.08,205.64,352.19)=約2.8m手前(ユーザー実機確定)。
                // CircleとNpcを分離してNPC衝突→走り続けを回避(distance:5判定 約2.9m<5)。
                NpcId = 1056826,
                Name = "Orbitingway",
                Location_Npc = new Vector3(290.94f, 206.21f, 349.36f),
                Location_Circle = new Vector3(291.08f, 205.64f, 352.19f),
            },
            [NpcType.Drone] = new NPCInfo // ドローン(Cosmodrone)交換NPC。実機確定 2026-06-03
            {
                NpcId = 1056828,
                Name = "Kaede",
                Location_Npc = new Vector3(302.20f, 205.64f, 398.55f),    // NPC実位置(/ice d Player Infoより)
                Location_Circle = new Vector3(302.47f, 205.64f, 396.07f), // アクセス立ち位置(ユーザー確定)
            },
            [NpcType.RedAlert] = new NPCInfo
            {
                // 目の前座標(実機確定 2026-06-02): (280.63, 205.64, 353.20)
                NpcId = 1056819,
                Name = "Lefleda",
                Location_Npc = new Vector3(280.41f, 205.64f, 352.50f),    // 2026-06-04 再取得(開拓で移動)
                Location_Circle = new Vector3(282.25f, 205.64f, 355.81f), // 実機で立って会話できた位置(NPCまで約3.8m。旧Circleは0.85mで衝突懸念)
            },
        },
    };

    public static Vector3 GetRandomPointInCircle(Vector3 center, float radius)
    {
        // Generate random angle (0 to 2π)
        float angle = Random.Shared.NextSingle() * MathF.PI * 2f;

        // Generate random distance from center
        // Use square root for uniform distribution
        float distance = MathF.Sqrt(Random.Shared.NextSingle()) * radius;

        // Convert polar to cartesian coordinates
        float x = center.X + distance * MathF.Cos(angle);
        float z = center.Z + distance * MathF.Sin(angle);

        return new Vector3(x, center.Y, z);
    }
}