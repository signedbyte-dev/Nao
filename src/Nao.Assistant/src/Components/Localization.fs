namespace Nao.Assistant

/// Application localization (i18n).
///
/// All user-facing UI strings funnel through a single `Strings` record so the app can be
/// translated by adding another language table — no view code changes required. Today only
/// English ships; the infrastructure (a `Language` union, a per-language table, and a
/// global current-language switch read fresh on every render) is ready to extend.
///
/// Usage in views: `let t = Localization.current ()` then `t.Send`, `t.Settings`, etc.
module Localization =

    /// Supported UI languages. Add a case here, a table below, and wire `tableFor`.
    type Language =
        | English

    /// Every localizable string in the app. Group by area for readability.
    type Strings =
        { // Shell / navigation
          NewSessionTooltip: string
          WorkshopTooltip: string
          SettingsTooltip: string
          NoSessionSelected: string
          // Session view
          ComposerPlaceholder: string
          Send: string
          Attach: string
          StartConversation: string
          LoadingHistory: string
          Working: string
          ThanksFeedback: string
          // Settings view
          SettingsTitle: string
          SettingsSubtitle: string
          Appearance: string
          Theme: string
          ThemeDark: string
          ThemeLight: string
          Language: string
          Provider: string
          Orchestrator: string
          Workspace: string
          Save: string
          Close: string
          // Workshop view
          WorkshopTitle: string
          Tools: string
          Agents: string
          Knowledge: string }

    /// English (default) string table.
    let private en : Strings =
        { NewSessionTooltip = "New session"
          WorkshopTooltip = "Workshop"
          SettingsTooltip = "Settings"
          NoSessionSelected = "No session selected"
          ComposerPlaceholder = "Describe what to build..."
          Send = "Send"
          Attach = "Attach a file"
          StartConversation = "Start a conversation..."
          LoadingHistory = "Loading conversation history..."
          Working = "Working..."
          ThanksFeedback = "Thanks for your feedback"
          SettingsTitle = "Settings"
          SettingsSubtitle = "Configure the model provider, orchestrator and workspace."
          Appearance = "Appearance"
          Theme = "Theme"
          ThemeDark = "Dark"
          ThemeLight = "Light"
          Language = "Language"
          Provider = "Provider"
          Orchestrator = "Orchestrator"
          Workspace = "Workspace"
          Save = "Save"
          Close = "Close"
          WorkshopTitle = "Workshop"
          Tools = "Tools"
          Agents = "Agents"
          Knowledge = "Knowledge" }

    let private tableFor (lang: Language) : Strings =
        match lang with
        | English -> en

    /// The active language. Switched from settings; read fresh on each render.
    let mutable currentLanguage = English

    /// The active string table.
    let current () : Strings = tableFor currentLanguage

    /// All selectable languages, in display order.
    let all : Language list = [ English ]

    /// Human-readable name of a language (in its own language).
    let displayName (lang: Language) : string =
        match lang with
        | English -> "English"

    /// Persistable code for a language.
    let code (lang: Language) : string =
        match lang with
        | English -> "en"

    /// Parse a persisted language code, defaulting to English.
    let parse (value: string) : Language =
        match (if isNull value then "" else value).Trim().ToLowerInvariant() with
        | "en" | "english" | "" -> English
        | _ -> English

    /// Switch the active language.
    let apply (lang: Language) = currentLanguage <- lang
