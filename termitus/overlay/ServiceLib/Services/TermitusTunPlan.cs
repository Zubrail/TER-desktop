namespace ServiceLib.Services;

/// <summary>
/// 0.6.4: which path «Защищать весь трафик» takes on Connect, as pure decisions (unit-tested).
/// Mac: the OS authorization dialog is shown when the core starts. Windows: the core needs
/// administrator rights, so TER offers one restart as administrator (UAC) and reconnects after it.
/// </summary>
public static class TermitusTunPlan
{
    public enum Step
    {
        /// <summary>Setting off, test instance or a platform without TUN support here: nothing to do.</summary>
        NotWanted,
        /// <summary>TUN is already on.</summary>
        AlreadyOn,
        /// <summary>Mac: turn TUN on; the system dialog appears when the core starts.</summary>
        MacAuthorize,
        /// <summary>Windows, already administrator: turn TUN on.</summary>
        WinEnableAsAdmin,
        /// <summary>Windows, not administrator: show TER's sheet, offer the elevated restart.</summary>
        WinAskRestart,
        /// <summary>Windows, user chose «Только браузеры» earlier in this run: do not ask again.</summary>
        BrowsersOnly,
    }

    public static Step Decide(bool allTraffic, bool isTestInstance, bool isMac, bool isWindows, bool tunAlreadyOn,
        bool isAdmin, bool browsersOnlyThisSession)
    {
        if (!allTraffic || isTestInstance || !(isMac || isWindows))
        {
            return Step.NotWanted;
        }
        if (tunAlreadyOn)
        {
            return Step.AlreadyOn;
        }
        if (isMac)
        {
            return Step.MacAuthorize;
        }
        if (isAdmin)
        {
            return Step.WinEnableAsAdmin;
        }
        return browsersOnlyThisSession ? Step.BrowsersOnly : Step.WinAskRestart;
    }

    public enum StartupStep { Nothing, ClearFlagOnly, ConnectAfterRestart }

    /// <summary>On launch: the one-shot «connect after elevated restart» flag.</summary>
    public static StartupStep OnStartup(bool flagSet, bool isAdmin, bool isWindows)
    {
        if (!flagSet)
        {
            return StartupStep.Nothing;
        }
        return isWindows && isAdmin ? StartupStep.ConnectAfterRestart : StartupStep.ClearFlagOnly;
    }

    public enum AskOutcome { Restarting, Declined, BrowsersOnly }

    /// <summary>After the Windows sheet: «Продолжить» + the elevated process really started = restart; UAC refused = declined.</summary>
    public static AskOutcome AfterAsk(bool choseContinue, bool elevatedProcessStarted)
    {
        if (!choseContinue)
        {
            return AskOutcome.BrowsersOnly;
        }
        return elevatedProcessStarted ? AskOutcome.Restarting : AskOutcome.Declined;
    }
}
