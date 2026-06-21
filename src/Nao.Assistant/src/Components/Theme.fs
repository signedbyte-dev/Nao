namespace Nao.Assistant

open Avalonia
open Avalonia.Media
open Avalonia.Styling

/// Centralized design tokens for the Nao desktop app.
///
/// Every color, radius, spacing step and type size used by the views lives here so the
/// UI stays visually consistent. Colors come from a single cohesive neutral family plus
/// one indigo accent. The color tokens are *mutable* so the whole app can switch between
/// a dark and a light palette at runtime (see `Theme.apply`); views read the tokens fresh
/// on every render, so an Elmish re-render after a switch repaints with the new palette.
/// Pair this with the Semi.Avalonia theme (wired in Program.fs) which styles the default
/// controls; these tokens cover the bespoke surfaces (chat bubbles, composer, traces).
module Theme =

    /// The two supported visual variants.
    type Variant =
        | Dark
        | Light

    let private color (hex: string) = Color.Parse hex
    let private brush (hex: string) : IBrush = SolidColorBrush(color hex) :> IBrush

    // --- Themed color tokens (assigned by `apply`) ----------------------------------
    // Initialized to placeholders; `apply Dark` runs at module load (bottom of file).
    let mutable bg : IBrush = Unchecked.defaultof<IBrush>
    let mutable surface : IBrush = Unchecked.defaultof<IBrush>
    let mutable surfaceRaised : IBrush = Unchecked.defaultof<IBrush>
    let mutable surfaceHover : IBrush = Unchecked.defaultof<IBrush>
    let mutable surfaceInset : IBrush = Unchecked.defaultof<IBrush>
    let mutable border : IBrush = Unchecked.defaultof<IBrush>
    let mutable borderSubtle : IBrush = Unchecked.defaultof<IBrush>
    let mutable borderStrong : IBrush = Unchecked.defaultof<IBrush>
    let mutable textPrimary : IBrush = Unchecked.defaultof<IBrush>
    let mutable textSecondary : IBrush = Unchecked.defaultof<IBrush>
    let mutable textMuted : IBrush = Unchecked.defaultof<IBrush>
    let mutable accentSoft : IBrush = Unchecked.defaultof<IBrush>
    let mutable info : IBrush = Unchecked.defaultof<IBrush>
    /// Accent for an orchestrator reasoning step in the execution trace.
    let mutable traceReasoning : IBrush = Unchecked.defaultof<IBrush>
    /// Accent for a sub-agent delegation step.
    let mutable traceAgent : IBrush = Unchecked.defaultof<IBrush>
    /// Accent for a tool-call step.
    let mutable traceTool : IBrush = Unchecked.defaultof<IBrush>

    // --- Constant tokens (same in both variants) ------------------------------------
    let accent = brush "#6366F1"
    let accentHover = brush "#4F46E5"
    let accentText = brush "#FFFFFF"
    let success = brush "#34D399"
    let warning = brush "#FBBF24"
    let danger = brush "#F87171"
    let transparent : IBrush = Brushes.Transparent :> IBrush

    // --- Radius ---------------------------------------------------------------------
    let radiusSm = 6.0
    let radiusMd = 10.0
    let radiusLg = 14.0

    // --- Spacing scale --------------------------------------------------------------
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0

    // --- Type scale -----------------------------------------------------------------
    let fontCaption = 11.0
    let fontSmall = 12.0
    let fontBody = 13.0
    let fontBodyLg = 15.0
    let fontTitle = 18.0
    let fontHeadline = 22.0

    /// The currently active variant.
    let mutable current = Dark

    let private setDark () =
        bg <- brush "#0A0A0C"
        surface <- brush "#141417"
        surfaceRaised <- brush "#1C1C21"
        surfaceHover <- brush "#26262C"
        surfaceInset <- brush "#101013"
        border <- brush "#2A2A31"
        borderSubtle <- brush "#1F1F24"
        borderStrong <- brush "#3A3A43"
        textPrimary <- brush "#F4F4F5"
        textSecondary <- brush "#A1A1AA"
        textMuted <- brush "#71717A"
        accentSoft <- brush "#312E81"
        info <- brush "#93C5FD"
        traceReasoning <- brush "#A78BFA"
        traceAgent <- brush "#FBBF24"
        traceTool <- brush "#93C5FD"

    let private setLight () =
        bg <- brush "#FAFAFA"
        surface <- brush "#FFFFFF"
        surfaceRaised <- brush "#F4F4F5"
        surfaceHover <- brush "#E4E4E7"
        surfaceInset <- brush "#F1F1F4"
        border <- brush "#D4D4D8"
        borderSubtle <- brush "#E4E4E7"
        borderStrong <- brush "#A1A1AA"
        textPrimary <- brush "#18181B"
        textSecondary <- brush "#52525B"
        textMuted <- brush "#71717A"
        accentSoft <- brush "#E0E7FF"
        info <- brush "#2563EB"
        traceReasoning <- brush "#7C3AED"
        traceAgent <- brush "#B45309"
        traceTool <- brush "#2563EB"

    /// Switch the whole app to the given variant. Re-render the UI afterwards (Elmish does
    /// this automatically when the owning model changes) to repaint with the new palette.
    let apply (variant: Variant) =
        current <- variant
        match variant with
        | Dark -> setDark ()
        | Light -> setLight ()
        // Keep the Semi.Avalonia-styled default controls (buttons, combos, scrollbars) in
        // sync with our bespoke surfaces.
        match Application.Current with
        | null -> ()
        | app ->
            app.RequestedThemeVariant <-
                (match variant with
                 | Dark -> ThemeVariant.Dark
                 | Light -> ThemeVariant.Light)

    /// Parse a persisted theme name ("Dark"/"Light"), defaulting to Dark.
    let parse (name: string) : Variant =
        match (if isNull name then "" else name).Trim().ToLowerInvariant() with
        | "light" -> Light
        | _ -> Dark

    /// The persistable name for a variant.
    let name (variant: Variant) : string =
        match variant with
        | Dark -> "Dark"
        | Light -> "Light"

    // Initialize the tokens to the dark palette at module load.
    do setDark ()
