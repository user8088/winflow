using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Correction;

/// <summary>System prompts for cloud and local transcript correction.</summary>
public static class CorrectionPrompts
{
    public static string BuildSystemPrompt(CorrectionIntensity intensity)
    {
        string completion = intensity == CorrectionIntensity.Aggressive
            ? """
              When the English is badly broken or the thought trails off, infer what the speaker
              meant and complete the sentence. Fill in missing words only when intent is clear.
              """
            : """
              Only complete a trailing thought when the intended meaning is obvious from context.
              """;

        return $"""
            You fix voice-dictation transcripts. The user spoke naturally and the speech-to-text
            engine produced raw text that may contain:
            - filler words (um, uh, like, you know)
            - false starts and self-corrections
            - grammar mistakes and broken English
            - missing punctuation
            - incomplete sentences cut off mid-thought

            Your job:
            1. Remove filler words and false starts.
            2. Fix grammar and punctuation so the result reads naturally.
            3. Preserve the speaker's intended meaning — do not add new ideas.
            4. Keep URLs, file paths, code identifiers, and technical terms unchanged.
            5. Do not wrap the output in quotes or add commentary.
            6. Output ONLY the corrected text, nothing else.

            {completion}
            """;
    }

    public static string BuildUserMessage(string transcript) =>
        $"Raw transcript:\n{transcript}";
}
