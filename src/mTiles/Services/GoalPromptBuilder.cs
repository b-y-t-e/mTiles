namespace mTiles.Services;

public sealed class GoalPromptBuilder
{
    private const string QualityRules =
        "All changes MUST follow Clean Code principles (descriptive naming, small single-purpose functions, " +
        "no duplication, self-documenting code) and SOLID principles — especially:\n" +
        "- Single Responsibility Principle: each class/method has one reason to change\n" +
        "- Open/Closed Principle: open for extension, closed for modification\n\n";

    public string BuildClarify(string goal, IReadOnlyList<string> clarificationHistory)
    {
        var prompt = $"You are helping implement a goal in a software project. The goal is:\n\n{goal}\n\n";
        if (clarificationHistory.Count > 0)
            prompt += $"Previous conversation:\n{string.Join("\n", clarificationHistory)}\n\n";
        prompt += "Your tasks:\n" +
                  "1. Verify the goal is specific, measurable, and achievable in code. If not, point out what is vague.\n" +
                  "2. Ask 2-3 short, specific clarifying questions to fill any gaps.\n" +
                  "3. If the goal is already fully clear and achievable, say so and confirm you have no questions.\n\n" +
                  "Be concise. Only ask questions, do not implement anything yet.";
        return prompt;
    }

    public string BuildPlan(string goal, IReadOnlyList<string> clarificationHistory)
    {
        var prompt = $"You are planning the implementation of a goal in a software project.\n\nOriginal goal:\n{goal}\n\n";
        if (clarificationHistory.Count > 0)
            prompt += $"User clarifications:\n{string.Join("\n", clarificationHistory)}\n\n";
        prompt += QualityRules;
        prompt += "Create a concise implementation plan:\n" +
                  "1. Restate the goal in one clear sentence\n" +
                  "2. List the concrete steps (files to create/modify, logic to add)\n" +
                  "3. State clear success criteria — how to verify the goal is met\n\n" +
                  "Be specific and actionable. Do not implement anything yet.";
        return prompt;
    }

    public string BuildImplement(string goal, string? approvedPlan, string? reviewFeedback, string? gitDiff)
    {
        var prompt = $"Implement the following goal in this project:\n\n{goal}\n\n";
        if (!string.IsNullOrEmpty(approvedPlan))
            prompt += $"Approved implementation plan:\n{approvedPlan}\n\n";
        prompt += QualityRules;
        if (gitDiff != null)
            prompt += $"Current changes in the project (git diff):\n```\n{gitDiff}\n```\n\n";
        if (reviewFeedback != null)
            prompt += $"Previous review feedback:\n{reviewFeedback}\n\n";
        prompt += "Follow the approved plan. Make the necessary code changes. Be precise and minimal.";
        return prompt;
    }

    public string BuildReview(string goal, string? gitDiff)
    {
        var prompt = $"Review the code changes that were just made in this project. The original goal was:\n\n{goal}\n\n";
        prompt += QualityRules;
        if (gitDiff != null)
            prompt += $"Current changes (git diff):\n```\n{gitDiff}\n```\n\n";
        prompt += "Check if the changes correctly implement the goal. Report:\n" +
                  "1. Whether the goal is fully met\n" +
                  "2. Any bugs or issues found\n" +
                  "3. Clean Code and SOLID violations found\n" +
                  "4. Your verdict: PASS or FAIL\n\n" +
                  "If PASS, say 'VERDICT: PASS'. If there are issues, say 'VERDICT: FAIL' and describe what needs fixing.";
        return prompt;
    }
}
