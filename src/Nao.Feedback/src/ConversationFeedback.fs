namespace Nao.Feedback

open System

/// Heuristic, deterministic extraction of *implicit* feedback from conversation history.
///
/// When a user reacts to a prior answer ("that's wrong", "perfect, thanks", "not what I
/// asked"), that reaction is a feedback signal even though the user never clicked a
/// good/bad button. This module detects such phrases in a turn's user text so the signal
/// can be persisted as a `Feedback` entry bound to the previous turn — feeding the same
/// cross-session enhancement pipeline as explicit feedback, with no LLM required.
module ConversationFeedback =

    /// Negative reaction phrases. Matched case-insensitively as substrings.
    let private negativePhrases =
        [ "that's wrong"; "thats wrong"; "that is wrong"
          "not what i asked"; "not what i wanted"; "not what i meant"
          "that's not right"; "thats not right"; "that is not right"
          "doesn't work"; "does not work"; "didn't work"; "did not work"
          "still broken"; "still wrong"; "still failing"
          "incorrect"; "not helpful"; "unhelpful"; "useless"
          "try again"; "that's not it"; "thats not it"
          "no, that's"; "no that's"; "wrong answer"; "this is wrong" ]

    /// Positive reaction phrases. Matched case-insensitively as substrings.
    let private positivePhrases =
        [ "perfect"; "that works"; "that worked"; "exactly"; "exactly what i"
          "thank you"; "thanks"; "great job"; "nice job"; "good job"
          "that's correct"; "thats correct"; "that is correct"
          "that's right"; "thats right"; "looks good"; "looks great"
          "well done"; "awesome"; "brilliant"; "love it"; "that's it"; "thats it" ]

    let private containsAny (haystack: string) (needles: string list) : string option =
        needles |> List.tryFind (fun n -> haystack.Contains(n, StringComparison.OrdinalIgnoreCase))

    /// Detect an implicit feedback signal in a piece of user text. Returns the inferred
    /// sentiment and the matched phrase (as an explanatory comment), or None when the text
    /// carries no clear reaction. Negative signals win ties, since an unaddressed problem is
    /// more important to surface than praise.
    let detect (text: string) : (FeedbackSentiment * string) option =
        if String.IsNullOrWhiteSpace text then None
        else
            match containsAny text negativePhrases with
            | Some phrase -> Some(FeedbackSentiment.Negative, sprintf "Implicit signal: \"%s\"" phrase)
            | None ->
                match containsAny text positivePhrases with
                | Some phrase -> Some(FeedbackSentiment.Positive, sprintf "Implicit signal: \"%s\"" phrase)
                | None -> None
