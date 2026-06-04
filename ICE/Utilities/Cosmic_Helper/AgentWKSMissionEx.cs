using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;
using System.Runtime.InteropServices;

namespace ICE.Utilities.Cosmic_Helper;

/// <summary>
/// Shim for AgentWKSMission members not yet in the shipped FFXIVClientStructs DLL.
/// Remove once upstream CS ships GetCriticalMissions.
/// 公式0.0.78.1より移植: ジョブタブ選択をUIクリック非依存のゲーム内部API直叩きにする(タブ選択不成立による棒立ち回避)。
/// </summary>
public static unsafe class AgentWKSMissionEx
{
    private delegate bool GetCriticalMissionsDelegate(AgentWKSMission* agent, StdVector<AgentWKSMission.MissionEntry>* list);
    private delegate byte JobIndexToClassJobIdDelegate(AgentWKSMission* agent, byte jobIndex);

    private static readonly GetCriticalMissionsDelegate? _getCriticalMissions;
    private static readonly JobIndexToClassJobIdDelegate? _jobIndexToClassJobId;

    static AgentWKSMissionEx()
    {
        try
        {
            var ptr = Svc.SigScanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B EA E8");
            _getCriticalMissions = Marshal.GetDelegateForFunctionPointer<GetCriticalMissionsDelegate>(ptr);
        }
        catch (Exception ex)
        {
            IceLogging.Error($"{ex.Message} | [AgentWKSMissionEx] Failed to scan GetCriticalMissions sig");
        }

        try
        {
            var ptr = Svc.SigScanner.ScanText("0F B6 C2 83 F8 0A");
            _jobIndexToClassJobId = Marshal.GetDelegateForFunctionPointer<JobIndexToClassJobIdDelegate>(ptr);
        }
        catch (Exception ex)
        {
            IceLogging.Error($"{ex.Message} | [AgentWKSMissionEx] Failed to scan JobIndexToClassJobId sig");
        }
    }

    /// <summary>
    /// Populates <paramref name="list"/> with the current critical missions, same as
    /// GetBasicMissions / GetProvisionalMissions. Returns false if the sig wasn't found.
    /// </summary>
    public static bool GetCriticalMissions(AgentWKSMission* agent, StdVector<AgentWKSMission.MissionEntry>* list)
    {
        if (_getCriticalMissions == null || agent == null) return false;
        return _getCriticalMissions(agent, list);
    }

    /// <summary>GetCriticalMissions シグネチャのスキャンに成功しているか。
    /// false の場合は「今 Red Alert が発生中か」をタブ切替なしで判定できないため、呼び出し側は従来挙動へフォールバックする。</summary>
    public static bool HasCriticalApi => _getCriticalMissions != null;

    /// <summary>
    /// Sets the agent's selected job tab by ClassJob ID (8–18), resolving it to the
    /// internal 0–11 job index. Also syncs MissionData and clears HasSavedTab.
    /// Returns false if the sig wasn't found or the agent/data is null.
    /// <para>
    /// selectBasicTab=true (公式既定) は上段タブを基本(0)へ強制する。MINEは basic/critical の上段タブを
    /// OpenCorrectTab 側で別管理しているため、ジョブサブタブのみAPIで決定したい場合は false を渡す
    /// (false にしないと critical タブ表示中に基本タブへ戻されてタブ選択がping-pongし棒立ちになる)。
    /// </para>
    /// </summary>
    public static bool SetSelectedJobTab(AgentWKSMission* agent, byte classJobId, bool selectBasicTab = true)
    {
        if (_jobIndexToClassJobId == null || agent == null || agent->Data == null) return false;

        byte jobIndex = 0;
        for (byte i = 0; i <= 11; i++)
        {
            if (_jobIndexToClassJobId(agent, i) != classJobId) continue;
            jobIndex = i;
            break;
        }

        if (selectBasicTab)
            agent->SelectedTab = 0;
        agent->Data->SelectedJobIndex = jobIndex;
        agent->Data->UpdateFlags = 1;
        // Clear HasSavedTab to prevent the game from overwriting our set on the next tick
        // HasSavedTab is private so we write it directly via offset (0x35)
        *(bool*)((byte*)agent + 0x35) = false;

        return true;
    }

    public static int selectedTab()
    {
        var agent = AgentWKSMission.Instance();

        if (_jobIndexToClassJobId == null || agent == null || agent->Data == null) return -1;
        return agent->SelectedTab;
    }

    /// <summary>上段タブ(0=Basic/1=Provisional/2=Critical/3=Mastership)をゲーム内部の SelectedTab フィールド直書きで切り替える。
    /// マスターシップタブ(3)は ECommons にボタン定義が無い/ボタンクリックが効かない環境向けの確実な切替手段。</summary>
    public static bool SetSelectedTab(byte tab)
    {
        var agent = AgentWKSMission.Instance();
        if (agent == null || agent->Data == null) return false;
        agent->SelectedTab = tab;
        agent->Data->UpdateFlags = 1;
        *(bool*)((byte*)agent + 0x35) = false; // HasSavedTab をクリアして次tickでの上書きを防ぐ
        return true;
    }
}
