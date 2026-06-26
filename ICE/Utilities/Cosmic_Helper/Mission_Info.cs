using System.Collections.Generic;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using ICE.Utilities.Cosmic_Helper;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Utilities.Cosmic_Helper;

public static partial class CosmicHelper
{
    // ミッション報告/放棄の瞬間 CurrentLunarMission は 0 を返す。SheetMissionDict[0] は KeyNotFoundException になるため、
    // TryGetValue でガードして null を返す(呼び出し側はミッション境界では CurrentLunarMission==0 を見て早期 return する)。
    public static CosmicInfo CurrentMissionInfo =>
        SheetMissionDict.TryGetValue(CurrentLunarMission, out var info) ? info : null;

    /// <summary>
    /// Gives the current mission that is active
    /// </summary>
    public static unsafe uint CurrentLunarMission
    {
        get
        {
            try
            {
                var manager = WKSManager.Instance();
                if (manager == null)
                    return 0; // or some default value

                return manager->State.CurrentMissionUnitRowId;
            }
            catch (AccessViolationException)
            {
                IceLogging.Error("We're currently getting access violations with this, so returning 0");
                return 0;
            }
            catch (Exception)
            {
                IceLogging.Error("Welp. Somehow not getting it still. Exception exit");
                return 0;
            }
        }
    }
    public static unsafe uint? CurrentBait => WKSManager.Instance()->State.FishingBait;
    // public static unsafe uint CurrentLunarDevelopment => ExcelHelper.DevGrade.GetRow(WKSManager.Instance()->State.DevGrade).Unknown6;
    public static unsafe uint CurrentLunarDevelopment = 0;

    public static int MaxXpKind = 7; // 2026.05.25パッチで7タイプに増加(旧6→新7, WKSResearchModule Size 0xB0→0xC8)

    public static Dictionary<int, string> ExpDictionary = new()
    {
        { 1, "I" },
        { 2, "II" },
        { 3, "III" },
        { 4, "IV" },
        { 5, "V" },
        { 6, "VI" },
        { 7, "VII" } // 2026.05.25パッチ追加
    };

    public static readonly Dictionary<uint, uint> PlanetCreditInfo = new()
    {
        [1237] = 45691, // sinus
        [1291] = 48146, // phaenna
        [1310] = 48147, // Oizys
        [1319] = 48148, // Auxesia (riri版より。45691/48146/48147に続く連番。万一誤りでもGetItemCount=0でガンバ不発になるだけで無害)
    };

    public class Dronebit
    {
        public uint creditId { get; set; } = 0;
        public uint boxId { get; set; } = 0;
    }

    public static readonly Dictionary<uint, Dronebit> DronebitInfo = new()
    {
        [1310] = new() // Oizys
        {
            creditId = 49170,
            boxId = 50414,
        },
        [1319] = new() // Auxesia (実機確認: Auxesia Dronebit=49171 / Auxesia Drone Module=50415)
        {
            creditId = 49171,
            boxId = 50415,
        }
    };

    // General use functions used across the codebase, specifically tied to cosmic related functions
    public static void OpenStellarMission()
    {
        if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var hud) && hud.IsAddonReady)
        {
            if (GenericHelpers.TryGetAddonMaster<WKSMissionInfomation>("WKSMissionInfomation", out var missionInfo) && missionInfo.IsAddonReady)
            {
                return;
            }
            else
            {
                if (EzThrottler.Throttle("Opening Stellar Missions"))
                {
                    IceLogging.Debug("Opening Mission Menu");
                    hud.Mission();
                }
            }
        }
    }

    public class ClassInfo
    {
        public int Score { get; set; } = 0;
        // マスターシップポイント(WKSScoreList[i].Unknown5が指すアイテムの所持数)。本家0.0.78.26より移植。
        public int Mastery { get; set; } = 0;
        public int Stage_Current { get; set; } = 0;
        public int Stage_Next { get; set; } = 0;
        public Dictionary<int, ExpInfo> CurrentExp { get; set; } = new();
    }

    public class ExpInfo
    {
        public string Name { get; set; } = "";
        public int Current { get; set; } = 0;
        public int Needed { get; set; } = 0;
        public int Max { get; set; } = 0;
    }

    public static unsafe Dictionary<uint, ClassInfo> Cosmic_ClassInfo()
    {
        Dictionary<uint, ClassInfo> cosmicClassInfo = new()
        {
            [8] = new(),
            [9] = new(),
            [10] = new(),
            [11] = new(),
            [12] = new(),
            [13] = new(),
            [14] = new(),
            [15] = new(),
            [16] = new(),
            [17] = new(),
            [18] = new(),
        };

        var wksManagerPtr = WKSManager.Instance();
        if (wksManagerPtr == null)
        {
            if (PlayerHelper.IsInCosmicZone())
            {
                if (EzThrottler.Throttle("Throttling log message", 3000))
                    IceLogging.Error("WKSManager returned null");
            }
            return cosmicClassInfo;
        }

        var wks = wksManagerPtr;
        var researchModule = wks->ResearchModule;

        if (researchModule == null || !researchModule->IsLoaded)
        {
            if (EzThrottler.Throttle("Throttling log message", 3000))
                IceLogging.Error("Research Module has returned null");
            return cosmicClassInfo;
        }

        // Use original pointer only for member function calls
        var researchModuleFuncs = researchModule;

        for (int i = 0; i < 11; i++)
        {
            uint jobId = (uint)i + 8;
            byte toolClassId = (byte)(jobId - 7);
            byte arrayIndex = (byte)(toolClassId - 1);

            var score = wks->State.Scores[arrayIndex];
            var currentStage = researchModule->CurrentStages[arrayIndex];
            var nextStage = currentStage == CosmicHelper.MaxRelicLevel
                ? CosmicHelper.MaxRelicLevel
                : (byte)(currentStage + 1);

            // マスターシップポイント(本家0.0.78.26移植): WKSScoreList[i].Unknown5 が各ジョブの
            // 「マスターシップポイント:{職}」アイテムID(51315〜51325)。その所持数を Mastery とする。
            int mastery = 0;
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.WKSScoreList>().GetRowOrDefault((uint)i) is { } scoreRow)
                PlayerHelper.GetItemCount(scoreRow.Unknown5, out mastery);

            ClassInfo entry = new()
            {
                Score = score,
                Mastery = mastery,
                Stage_Current = currentStage,
                Stage_Next = nextStage,
            };

            for (byte type = 1; type <= MaxXpKind; type++)
            {
                if (!researchModuleFuncs->IsTypeAvailable(toolClassId, type))
                {
                    break;
                }

                entry.CurrentExp[type] = new()
                {
                    Name = ExpDictionary.TryGetValue(type, out var expName) ? expName : "???",
                    Needed = researchModuleFuncs->GetNeededAnalysis(toolClassId, type),
                    Current = researchModuleFuncs->GetCurrentAnalysis(toolClassId, type),
                    Max = researchModuleFuncs->GetMaxAnalysis(toolClassId, type),
                };
            }

            cosmicClassInfo[jobId] = entry;
        }

        return cosmicClassInfo;
    }
    public unsafe static void Update_MissionCompletion()
    {
        foreach (var mission in CosmicHelper.SheetMissionDict)
            mission.Value.CompletionStatus = CosmicHandler.MissionStatus(mission.Key);
    }
    public static unsafe bool Task_UpdateRelicMissionInfo()
    {
        string tag = "Task: Update Cosmic Info";

        if (PlayerHelper.IsInCosmicZone())
        {
            if (PlayerHelper.IsScreenReady())
            {
                var wksManagerPtr = WKSManager.Instance();
                if (wksManagerPtr == null)
                {
                    if (EzThrottler.Throttle("Update Stats"))
                        IceLogging.Verbose("Waiting for the wksManager to be loaded", tag);

                    return false;
                }
                else
                {
                    Update_MissionCompletion();
                    // IceLogging.Verbose("Updated cosmic dictionary to have proper values", tag);
                    return true;
                }
            }
            else
            {
                IceLogging.Verbose("Waiting for screen to be ready...", tag);
                return false;
            }
        }
        else
        {
            IceLogging.Verbose("We're not in a cosmic area, so we're going to just exit this check", tag);
            return true;
        }
    }
}
