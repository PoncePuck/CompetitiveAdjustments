// DashFall.ChatSuppress.cs - keeps the game's chat from opening on top of the config panel.

using HarmonyLib;

namespace DashFallMod.Client
{
    /// <summary>
    /// Blocks the two chat-open actions while the F4 config panel is up.
    ///
    /// The panel already suppresses ordinary gameplay input by holding
    /// GlobalStateManager.UIState.IsMouseRequired, which is what PlayerInput.UpdateInputs checks,
    /// and that covers the quick chats because they run through PlayerInput. All Chat and Team Chat
    /// do not: UIManager subscribes to those two InputActions directly in its own Awake, and its
    /// handlers gate only on the UI phase and on GetTopmostBlockingInteractingView. This panel is
    /// not a UIView, so it contributes nothing to that check and chat opened straight over it,
    /// leaving two focused text fields on screen competing for the same keystrokes.
    ///
    /// Patching the two handlers rather than disabling the InputActions is deliberate. Disabling an
    /// action means owning the job of re-enabling it, and any path that closes the panel without
    /// running our restore (a scene change, an exception, the mod being toggled off mid-session)
    /// would leave the player with chat permanently dead and no way to tell why.
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "OnAllChatActionPerformed")]
    public static class AllChatSuppressWhilePanelOpen
    {
        [HarmonyPrefix]
        public static bool Prefix() => !DashFallClientRunner.IsPanelOpenStatic;
    }

    [HarmonyPatch(typeof(UIManager), "OnTeamChatActionPerformed")]
    public static class TeamChatSuppressWhilePanelOpen
    {
        [HarmonyPrefix]
        public static bool Prefix() => !DashFallClientRunner.IsPanelOpenStatic;
    }
}
