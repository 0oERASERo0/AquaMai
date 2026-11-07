using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AquaMai.Config.Attributes;
using AquaMai.Core;
using AquaMai.Core.Attributes;
using AquaMai.Core.Helpers;
using HarmonyLib;
using Manager;
using MelonLoader;
using MelonLoader.TinyJSON;
using Monitor;
using Monitor.ModeSelect;
using Net.Packet;
using Net.Packet.Mai2;
using Net.VO.Mai2;
using Process;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace AquaMai.Mods.GameSystem;

[ConfigSection(
    name: "自由模式點數",
    en: """
        Gate Freedom Mode behind a server-managed credit balance.
        The cabinet asks the Freedom Credit API for the player's balance on card-in,
        greys out the Freedom Mode card when the balance is not positive, and deducts
        one credit the moment the player confirms Freedom Mode.
        NOTE: Disable [GameSystem.EnableFreedomMode] when this section is on —
        that module unconditionally enables Freedom Mode and would defeat the gate.
        """,
    zh: """
        以伺服器管理的點數餘額管制自由模式。
        刷卡時向 Freedom Credit API 查詢餘額，餘額不足時把自由模式卡片反灰，
        並在玩家確認選擇自由模式的當下扣掉一點。
        注意：啟用本欄目時請把 [GameSystem.EnableFreedomMode] 關掉 ——
        該模組會無條件開放自由模式，會讓本模組的管制形同虛設。
        """)]
public static class FreedomCredit
{
    [ConfigEntry(
        name: "API 網址",
        en: "Base URL of the Freedom Credit API (the LAN-only :8080 port). No trailing slash needed.",
        zh: "Freedom Credit API 的位址（僅綁 LAN 的 :8080）。結尾的斜線加不加都可以")]
    private static readonly string ApiUrl = "http://nas.lan:8080";

    [ConfigEntry(
        name: "API 金鑰",
        en: "Shared API key, sent as the X-Api-Key header. All cabinets share one key.",
        zh: "共用的 API 金鑰，以 X-Api-Key 標頭送出。兩台機台共用一把，外洩時一起換")]
    private static readonly string ApiKey = "";

    [ConfigEntry(
        name: "機台代號",
        en: "Cabinet identifier, recorded in the ledger as \"game:<CabinetId>\". For auditing only — the server cannot verify it.",
        zh: "機台代號，會記在流水帳的 actor 欄位（game:<機台代號>）。僅供排查，伺服器無從驗證")]
    private static readonly string CabinetId = "cab-01";

    [ConfigEntry(
        name: "請求逾時秒數",
        en: "HTTP timeout in seconds for every API call. Keep it short — these calls sit on the card-in path.",
        zh: "每次 API 請求的逾時秒數。請保持短，這些請求卡在刷卡流程上")]
    private static readonly int TimeoutSeconds = 5;

    [ConfigEntry(
        name: "連不上時放行",
        en: """
            What to do when the balance is unknown (API unreachable, timed out, or still loading).
            false (default): fail closed — the Freedom Mode card stays greyed out. This is what SPEC section 2 chose.
            true: fail open — Freedom Mode is playable for free. Only turn this on if you would rather
            give away free Freedom sessions than have the cabinet half-broken when the NAS is down.
            """,
        zh: """
            餘額未知（連不上 API、逾時、或還在查詢中）時的行為。
            false（預設）：fail-closed，自由模式卡片維持反灰。此為 SPEC §2 的決定。
            true：fail-open，照樣免費開放自由模式。只有在「NAS 掛掉時寧可送免費場次也不要機台半殘」
            的前提下才打開它
            """)]
    private static readonly bool FailOpenWhenUnavailable = false;

    [ConfigEntry(
        name: "點數區顯示餘額",
        en: "Replace the \"FREE PLAY\" text in the top-right credit area with the player's Freedom credit balance after card-in.",
        zh: "刷卡後，把右上角點數區塊的 FREE PLAY 字樣換成該玩家的自由模式點數餘額")]
    private static readonly bool ShowBalanceOnCreditArea = true;

    [ConfigEntry(
        name: "選單顯示餘額",
        en: "Show the balance on the Freedom Mode card in the mode-select carousel (the per-card credit number, which free play leaves at 0).",
        zh: "在模式選擇畫面的自由模式卡片上顯示餘額（借用 free play 下固定為 0 的那個點數數字）")]
    private static readonly bool ShowBalanceOnModeSelect = true;

    [ConfigEntry(
        name: "餘額文字格式",
        en: "Format of the credit-area balance text. {0} is replaced with the balance.",
        zh: "右上角餘額文字的格式，{0} 會被換成餘額數字")]
    private static readonly string BalanceTextFormat = "FREEDOM {0}";

    // 餘額未知時用 -1 表示，不要用 0 —— 0 是「確定沒點數」，語意不同
    private const int BalanceUnknown = -1;

    // ModeSelectType.Freedom。EnableFreedomMode.cs 與反編譯結果（ModeSelectProcess.cs:408）都用 2
    private const int FreedomModeSelectType = 2;

    private const string LogPrefix = "[FreedomCredit]";

    private class PlayerState
    {
        public bool HasIdentity;
        public ulong UserId;
        public string AccessCode;
        public uint AimeId;
        public string DisplayName;
        public int Balance = BalanceUnknown;

        // 一場 Freedom 一把 GUID。伺服器用它做 idempotency，所以重送絕對安全。
        public string SessionKey;
        public bool DeductStarted;

        public void Reset()
        {
            HasIdentity = false;
            UserId = 0;
            AccessCode = null;
            AimeId = 0;
            DisplayName = null;
            Balance = BalanceUnknown;
            SessionKey = null;
            DeductStarted = false;
        }
    }

    // 官方自由模式只支援單人，所以實際上只會用到 slot 0；
    // 仍然做成每個玩家位一份，避免雙人登入時把彼此的餘額蓋掉。
    private static readonly PlayerState[] Players = [new PlayerState(), new PlayerState()];

    private static readonly List<ModeSelectMonitor> ModeSelectMonitors = [];

    public static void OnAfterPatch()
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            MelonLogger.Warning($"{LogPrefix} ApiKey 是空的，伺服器會回 401，自由模式將永遠不開放");
        }

        // AquaMai 沒有「模組互斥」的機制，只能在這裡出聲。
        // 本模組的 ModeSelect patch 寫成與執行順序無關（見 PostModeSelectMonitorInitialize），
        // 所以同時啟用不會壞掉；但 EnableFreedomMode 會讓卡片無條件保持可選，管制形同虛設。
        try
        {
            if (ConfigLoader.Config.GetSectionState(typeof(EnableFreedomMode)).Enabled)
            {
                MelonLogger.Error($"{LogPrefix} [GameSystem.EnableFreedomMode] 同時啟用中！");
                MelonLogger.Error($"{LogPrefix} 它會無條件開放自由模式，點數管制會失效。請在 AquaMai.toml 中刪掉該欄目。");
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"{LogPrefix} 無法檢查 EnableFreedomMode 的啟用狀態：{e.Message}");
        }
    }

    #region 身分擷取與登入

    // 反編譯確認（Net.Packet.Mai2/PacketGetUserPreview.cs:21）：第二個 string 參數是 authKey，
    // 會被寫進 netQuery.Request.segaIdAuthKey —— 是憑證，不是卡號，絕對不要當成 accessCode 送出去。
    // 真正的卡號在 UserData.Detail.AccessCode（Manager.UserDatas/UserDetail.cs:121，string）。
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PacketGetUserPreview), MethodType.Constructor, typeof(ulong), typeof(string), typeof(Action<ulong, UserPreviewResponseVO>), typeof(Action<PacketStatus>))]
    public static void PostPacketGetUserPreview(ulong userId)
    {
        MelonLogger.Msg($"{LogPrefix} 刷卡登入 userId={userId}");
        MelonCoroutines.Start(LoginCoroutine(userId));
    }

    // 離開遊戲流程（回到待機畫面）與進入刷卡畫面時，把所有玩家位清乾淨，
    // 避免上一位玩家的餘額被下一位沿用。
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AdvertiseProcess), "OnStart")]
    public static void PreAdvertiseProcessOnStart() => ResetAllPlayers();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EntryProcess), "OnStart")]
    public static void PreEntryProcessOnStart() => ResetAllPlayers();

    private static void ResetAllPlayers()
    {
        foreach (var player in Players) player.Reset();
        ModeSelectMonitors.Clear();
    }

    private static IEnumerator LoginCoroutine(ulong userId)
    {
        // 顯示名稱在刷卡當下通常還沒填進 UserData，但伺服器的 displayName 是必填欄位
        // （src/lib/validate.ts 的 str()，空字串會回 400），所以在這裡等一小段時間。
        // 等待有上限，逾時就先用 fallback 名稱送出 —— 餘額要趕在 ModeSelect 之前拿到，
        // 不能為了一個名字把整條流程卡住。
        var slot = 0;
        var displayName = (string)null;
        var accessCode = (string)null;
        var aimeId = 0u;

        var deadline = Time.realtimeSinceStartup + Math.Max(0.5f, TimeoutSeconds * 0.5f);
        while (Time.realtimeSinceStartup < deadline)
        {
            if (TryReadUserData(userId, ref slot, ref displayName, ref accessCode, ref aimeId)) break;
            yield return null;
        }

        var state = Players[slot];
        state.UserId = userId;
        state.AccessCode = accessCode;
        state.AimeId = aimeId;
        state.DisplayName = displayName;
        state.HasIdentity = true;

        yield return PostLogin(state);

        // 名字可能在登入請求送出之後才填好。補送一次（upsert 是冪等的）讓名冊好查，
        // 同時順便刷新餘額。
        var nameDeadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < nameDeadline)
        {
            // 玩家已經離開（reset 跑過了），別再用這份過期的身分送請求
            if (!state.HasIdentity || state.UserId != userId) yield break;

            var laterSlot = slot;
            var laterName = (string)null;
            var laterAccessCode = (string)null;
            var laterAimeId = 0u;
            if (TryReadUserData(userId, ref laterSlot, ref laterName, ref laterAccessCode, ref laterAimeId) &&
                !string.IsNullOrEmpty(laterName) && laterName != state.DisplayName)
            {
                state.DisplayName = laterName;
                state.AccessCode = laterAccessCode;
                state.AimeId = laterAimeId;
                yield return PostLogin(state);
                yield break;
            }

            yield return null;
        }
    }

    // 反編譯確認（Manager.UserDatas/UserDetail.cs）：UserID 是 ulong（:96）、
    // UserName 是 string（:119）、AccessCode 是 string（:121）；
    // UserData.AimeId.Value 是 uint（AquaMai.Core/Helpers/PlayerPrefsStorage.cs:14 已有前例）。
    private static bool TryReadUserData(ulong userId, ref int slot, ref string displayName, ref string accessCode, ref uint aimeId)
    {
        // 第一輪：找 UserID 真的對得上的玩家位
        for (var i = 0; i < 2; i++)
        {
            if (!MatchesUserId(i, userId)) continue;
            if (!TryTakeUserData(i, ref slot, ref displayName, ref accessCode, ref aimeId)) continue;
            return true;
        }

        // 第二輪：UserID 比對不出結果（型別不符、或還沒填好）時退回 slot 0。
        // 官方自由模式只有單人，slot 0 就是那個人。
        return TryTakeUserData(0, ref slot, ref displayName, ref accessCode, ref aimeId);
    }

    private static bool MatchesUserId(int index, ulong userId)
    {
        try
        {
            var userData = UserDataManager.Instance.GetUserData(index);
            if (userData == null || !userData.IsEntry) return false;
            return userData.Detail.UserID == userId;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTakeUserData(int index, ref int slot, ref string displayName, ref string accessCode, ref uint aimeId)
    {
        try
        {
            var userData = UserDataManager.Instance.GetUserData(index);
            if (userData == null || !userData.IsEntry) return false;

            var name = userData.Detail?.UserName;
            if (string.IsNullOrEmpty(name)) return false;

            slot = index;
            displayName = name;
            accessCode = userData.Detail.AccessCode;
            aimeId = userData.AimeId.Value;
            return true;
        }
        catch
        {
            // UserData 還沒準備好，下一幀再試
            return false;
        }
    }

    private static IEnumerator PostLogin(PlayerState state)
    {
        if (!state.HasIdentity || state.UserId == 0) yield break;

        var body = new StringBuilder();
        body.Append('{');
        AppendJsonString(body, "userId", state.UserId.ToString());
        body.Append(',');
        AppendJsonString(body, "accessCode", state.AccessCode ?? "");
        body.Append(',');
        body.Append("\"aimeId\":").Append(state.AimeId);
        body.Append(',');
        AppendJsonString(body, "displayName", FallbackDisplayName(state));
        body.Append(',');
        AppendJsonString(body, "cabinetId", CabinetId);
        body.Append('}');

        var result = new ApiResult();
        yield return Post("/api/game/login", body.ToString(), result);

        if (!result.Ok)
        {
            MelonLogger.Warning($"{LogPrefix} 登入查詢失敗（{result.Describe()}），餘額維持未知");
            yield break;
        }

        if (!TryParseBalance(result.Body, out var balance))
        {
            MelonLogger.Warning($"{LogPrefix} 無法解析登入回應：{result.Body}");
            yield break;
        }

        state.Balance = balance;
        MelonLogger.Msg($"{LogPrefix} userId={state.UserId} 餘額 {balance}");
        RefreshModeSelect();
    }

    private static string FallbackDisplayName(PlayerState state)
    {
        return string.IsNullOrEmpty(state.DisplayName) ? $"user-{state.UserId}" : state.DisplayName;
    }

    #endregion

    #region 入場判定

    // 反編譯確認（docs/decompile-findings.md Q2）：_modeSelectTypeEnable[i] == false 的效果是
    // 「卡片變灰但仍可見、仍可捲到」，被擋住的是 OK 鍵 —— 不是隱藏，所以不需要額外的提示彈窗。
    //
    // 停用一律走 public 的 SetModeSelectTypeDisable(2)，它會自己做 SearchModeIndex，
    // 比直接寫 ____modeSelectTypeEnable 安全（index 不等於 modeSelectType）。
    //
    // 執行順序：這個 postfix 寫成與 EnableFreedomMode 的 postfix 無關 ——
    // 誰先跑都不會錯。我們先確保 type 2 在清單裡（不在就自己補），再套用停用；
    // 若 EnableFreedomMode 後跑，它的 Contains(2) 守衛會讓它什麼都不做。
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ModeSelectMonitor), "Initialize")]
    public static void PostModeSelectMonitorInitialize(ModeSelectMonitor __instance, List<int> ____modeSelectBaseType, List<bool> ____modeSelectTypeEnable)
    {
        if (!ModeSelectMonitors.Contains(__instance)) ModeSelectMonitors.Add(__instance);

        if (!____modeSelectBaseType.Contains(FreedomModeSelectType))
        {
            ____modeSelectBaseType.Add(FreedomModeSelectType);
            ____modeSelectTypeEnable.Add(true);
        }

        var allowed = IsFreedomAllowed(out var reason);
        MelonLogger.Msg($"{LogPrefix} ModeSelect：自由模式 {(allowed ? "開放" : "反灰")}（{reason}）");

        // 這裡不自己呼叫 SetMiniSelector()：卡片清單（_objModeSelectTypeListCard）要等
        // ModeSelectProcess 隨後的 InitSelector() 才會填好，原生流程會緊接著自己刷新一次。
        if (!allowed) __instance.SetModeSelectTypeDisable(FreedomModeSelectType);
    }

    // 餘額是非同步回來的，可能晚於 Initialize；而 SetMiniSelector 在每次捲動時都會被呼叫，
    // 所以在它跑之前重新套用一次判定，畫面就會自我修正。
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ModeSelectMonitor), nameof(ModeSelectMonitor.SetMiniSelector))]
    public static void PreSetMiniSelector(ModeSelectMonitor __instance, List<int> ____modeSelectBaseType, List<bool> ____modeSelectTypeEnable)
    {
        try
        {
            ApplyFreedomEnableState(__instance, ____modeSelectBaseType, ____modeSelectTypeEnable);
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"{LogPrefix} 套用自由模式開放狀態失敗：{e.Message}");
        }
    }

    private static void ApplyFreedomEnableState(ModeSelectMonitor instance, List<int> baseType, List<bool> typeEnable)
    {
        var index = baseType.IndexOf(FreedomModeSelectType);
        if (index < 0 || index >= typeEnable.Count) return;

        var allowed = IsFreedomAllowed(out _);
        if (typeEnable[index] == allowed) return;

        if (!allowed)
        {
            instance.SetModeSelectTypeDisable(FreedomModeSelectType);
            return;
        }

        // 遊戲只提供「停用」的 public API，沒有對應的「啟用」。
        // 餘額晚到且為正數時（我們已經先反灰了）只能自己把旗標寫回去。
        typeEnable[index] = true;
    }

    // 餘額非同步抵達時主動刷新一次畫面，不必等玩家捲動
    private static void RefreshModeSelect()
    {
        for (var i = ModeSelectMonitors.Count - 1; i >= 0; i--)
        {
            var monitor = ModeSelectMonitors[i];
            if (monitor == null)
            {
                ModeSelectMonitors.RemoveAt(i);
                continue;
            }

            try
            {
                // SetMiniSelector 的 prefix 會先把開放狀態重算一次，再由原方法重畫卡片
                monitor.SetMiniSelector();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"{LogPrefix} 刷新 ModeSelect 失敗：{e.Message}");
            }
        }
    }

    private static bool IsFreedomAllowed(out string reason)
    {
        var state = ActivePlayer();
        if (state == null)
        {
            reason = "未刷卡";
            return FailOpenWhenUnavailable;
        }

        if (state.Balance == BalanceUnknown)
        {
            reason = FailOpenWhenUnavailable ? "餘額未知，fail-open" : "餘額未知，fail-closed";
            return FailOpenWhenUnavailable;
        }

        reason = $"餘額 {state.Balance}";
        return state.Balance > 0;
    }

    private static PlayerState ActivePlayer()
    {
        foreach (var player in Players)
        {
            if (player.HasIdentity) return player;
        }

        return null;
    }

    #endregion

    #region 扣款

    // 反編譯確認（docs/decompile-findings.md Q1）：
    // GameManager.IsFreedomMode 的 setter 在正式流程中只有一個寫入 true 的地方 ——
    // ModeSelectProcess.OnUpdate / case ProcessState.Disp，而該區塊進入前就先把
    // _state 設成 ProcessState.Released，所以一個 session 保證只跑一次。
    //
    // 不要改掛回 GameManager.GetFreedomStartTime：那是純函式，一個 session 內會被呼叫 3 次
    // （兩台 PleaseWaitMonitor 的開場動畫各一次 + 計時器啟動一次），掛在那裡會扣三次。
    // 時長仍由 Utils/FreedomTimer.cs 覆寫 GetFreedomStartTime 負責，與本模組無交集。
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.IsFreedomMode), MethodType.Setter)]
    public static void PostSetIsFreedomMode(bool value)
    {
        // GameManager.Initialize() 會寫入 false，DebugGameMainObject 也會寫入 —— 只認 true
        if (!value) return;

        try
        {
            var state = ActivePlayer();
            if (state == null || !state.HasIdentity) return;

            // 縱深防禦：這個旗標擋重複扣款，伺服器端的 session_key UNIQUE 再擋一次
            if (state.DeductStarted) return;
            state.DeductStarted = true;
            state.SessionKey = Guid.NewGuid().ToString("D");

            MelonCoroutines.Start(DeductCoroutine(state));
        }
        catch (Exception e)
        {
            // setter 的 postfix 絕對不能往外丟例外，否則會影響遊戲本身的流程
            MelonLogger.Error($"{LogPrefix} 扣款流程啟動失敗：{e}");
        }
    }

    private static IEnumerator DeductCoroutine(PlayerState state)
    {
        var body = new StringBuilder();
        body.Append('{');
        AppendJsonString(body, "userId", state.UserId.ToString());
        body.Append(',');
        AppendJsonString(body, "sessionKey", state.SessionKey);
        body.Append(',');
        AppendJsonString(body, "cabinetId", CabinetId);
        body.Append('}');
        var payload = body.ToString();

        // 網路層失敗可以安全重送 —— sessionKey 相同，伺服器只會扣一次
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = new ApiResult();
            yield return Post("/api/game/deduct", payload, result);

            if (result.Status == 409)
            {
                // 餘額不足。入場已經由 ModeSelect 擋過一次，走到這裡代表
                // 餘額在兩個動作之間被改掉了（另一台機台、或管理員收回）。
                MelonLogger.Warning($"{LogPrefix} userId={state.UserId} 餘額不足，扣款被拒");
                state.Balance = 0;
                MessageHelper.ShowMessage("自由模式點數不足\n請向管理員領取點數", title: "FREEDOM CREDIT");
                yield break;
            }

            if (result.Ok)
            {
                if (TryParseBalance(result.Body, out var balance)) state.Balance = balance;
                MelonLogger.Msg($"{LogPrefix} 扣款完成 sessionKey={state.SessionKey} 餘額 {state.Balance}");
                yield break;
            }

            MelonLogger.Warning($"{LogPrefix} 扣款失敗（第 {attempt} 次，{result.Describe()}）");

            // 4xx（除了 409）重送也不會變好，只有網路錯誤與 5xx 值得重試
            if (result.Status >= 400 && result.Status < 500) yield break;
            yield return new WaitForSeconds(1f);
        }

        // 這場已經開始了，收不回來。帳對不上時以伺服器的流水為準，
        // 這一行是唯一的線索，所以用 Error 級別。
        MelonLogger.Error($"{LogPrefix} 扣款三次皆失敗，這一場沒有計費 sessionKey={state.SessionKey} userId={state.UserId}");
    }

    #endregion

    #region 餘額顯示

    // 反編譯確認（docs/decompile-findings.md Q4）：
    // CreditController 只有一個 TextMeshProUGUI —— _creditText。_freePlayText 與 CreditText
    // 都只是 readonly string，不是 UI 物件。Free play 時 SetFreePlayMode(true) 把
    // _creditText.text 寫成 _freePlayText 一次，之後 SetCredits 因為 `|| _freePlayMode`
    // 永遠 early-return —— 所以 patch SetCredits 在這些機台上完全沒作用。
    // ViewUpdate 則是每幀經 CommonProcess.OnUpdate → CommonMonitor.ViewUpdate 呼叫，
    // 寫在它的 postfix 必定生效，也能跟上非同步抵達的餘額。
    [EnableIf(nameof(ShowBalanceOnCreditArea))]
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CreditController), "ViewUpdate")]
    public static void PostCreditControllerViewUpdate(TextMeshProUGUI ____creditText)
    {
        if (____creditText == null) return;

        var state = ActivePlayer();
        // 未刷卡或餘額未知 —— 留著原本的 FREE PLAY，不要蓋掉
        if (state == null || state.Balance == BalanceUnknown) return;

        var text = FormatBalance(BalanceTextFormat, state.Balance, $"FREEDOM {state.Balance}");
        // 每幀都會進來，只在真的變了才寫回去，省掉 TMP 的重新排版
        if (____creditText.text != text) ____creditText.text = text;
    }

    // 反編譯確認（docs/decompile-findings.md Q3）：
    // 每張 mini 卡的點數數字 TMP 位於 _nullListMiniCard[i].transform.GetChild(0).GetChild(1)，
    // 而 free play 下 GetSelectorCreditNum 固定回傳 0，所以那個欄位是閒置的 —— 拿來放餘額。
    // SetMiniSelector 在初始化與每次捲動時都會重跑，文字會自動跟著刷新。
    [EnableIf(nameof(ShowBalanceOnModeSelect))]
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ModeSelectMonitor), nameof(ModeSelectMonitor.SetMiniSelector))]
    public static void PostSetMiniSelector(List<GameObject> ____nullListMiniCard, List<int> ____objModeSelectTypeListCard)
    {
        var state = ActivePlayer();
        if (state == null || state.Balance == BalanceUnknown) return;
        if (____nullListMiniCard == null || ____objModeSelectTypeListCard == null) return;

        // 原生程式碼就是用 <color=...> 標籤包數字，沿用同一套寫法
        var color = state.Balance > 0 ? "#ffffff" : "#a9a9a9";
        var text = $"<color={color}>{state.Balance}</color>";

        var count = Math.Min(____nullListMiniCard.Count, ____objModeSelectTypeListCard.Count);
        for (var i = 0; i < count; i++)
        {
            if (____objModeSelectTypeListCard[i] != FreedomModeSelectType) continue;

            try
            {
                var card = ____nullListMiniCard[i];
                if (card == null) continue;
                var tmp = card.transform.GetChild(0).GetChild(1).gameObject.GetComponent<TextMeshProUGUI>();
                if (tmp != null) tmp.text = text;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"{LogPrefix} 寫入自由模式卡片餘額失敗：{e.Message}");
            }
        }
    }

    private static string FormatBalance(string format, int balance, string fallback)
    {
        try
        {
            return string.Format(format, balance);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    #endregion

    #region API

    private class ApiResult
    {
        public int Status;      // 0 代表連線層就失敗了
        public string Body;
        public string Error;

        public bool Ok => Status >= 200 && Status < 300;

        public string Describe() => Status == 0 ? $"network error: {Error}" : $"HTTP {Status}: {Body}";
    }

    private static IEnumerator Post(string path, string body, ApiResult result)
    {
        var url = (ApiUrl ?? "").TrimEnd('/') + path;

        using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("X-Api-Key", ApiKey ?? "");
        request.timeout = TimeoutSeconds > 0 ? TimeoutSeconds : 5;

        yield return request.SendWebRequest();

        // 只看 isNetworkError：409 是我們要讀的業務狀態，不能被 isHttpError 吃掉
        if (request.isNetworkError)
        {
            result.Status = 0;
            result.Error = request.error;
            yield break;
        }

        result.Status = (int)request.responseCode;
        result.Body = request.downloadHandler?.text;
    }

    private class BalanceResponse
    {
        public int balance = 0;
    }

    // 反編譯確認（MelonLoader.dll / TinyJSON.JSON）：DecodeType<T> 對 primitive 走
    // Convert.ChangeType(data, typeof(T))，Variant 實作 IConvertible、數字由 ProxyNumber 包裝，
    // 所以 public int 欄位可以正常填入（DecodeFields 只認 public 或帶 [Include] 的欄位）。
    private static bool TryParseBalance(string json, out int balance)
    {
        balance = 0;
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            JSON.MakeInto<BalanceResponse>(JSON.Load(json), out var parsed);
            if (parsed == null) return false;
            balance = parsed.balance;
            return true;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"{LogPrefix} JSON 解析失敗：{e.Message}");
            return false;
        }
    }

    // 手刻 JSON，只為了四個字串欄位。AquaMai 目標是 net472 且沒有 Newtonsoft，
    // TinyJSON 的 JSON.Dump 對匿名型別不可靠，所以用最笨但最確定的作法。
    private static void AppendJsonString(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":");
        sb.Append('"');
        foreach (var c in value ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }

    #endregion
}
