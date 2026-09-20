using System.Collections.Immutable;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Conversation;

/// <summary>
/// Folds one event into a conversation. Pure: the same events in the same order are always the same
/// picture.
/// </summary>
/// <remarks>
/// <para><b>This is the rule a later web view must share</b>, which is why it lives in this assembly and
/// knows nothing of Avalonia: grouping tool calls between messages, closing what a turn left open, and
/// deciding what is still waiting on the user are all decided here once.</para>
/// <para>Three rules carry most of the weight:</para>
/// <list type="bullet">
/// <item><b>Work between two messages is one group.</b> A tool call, a thought or an answered approval
/// goes into the group the timeline ends with, or starts one. A message closes it.</item>
/// <item><b>A turn that ends closes everything it opened</b> — a streaming message stops streaming, a
/// running tool is abandoned, and approvals or questions nobody answered are dropped. A session whose
/// process has gone does the same. Left open, a restarted tile would show a spinner and an Allow button
/// for a request no process is waiting on.</item>
/// <item><b>Events for things that do not exist are ignored</b>, never thrown on. An agent that reports a
/// tool's end without its start, or a replay that starts mid-conversation, costs a line, not the view.
/// </item>
/// </list>
/// </remarks>
public static class ConversationReducer
{
    /// <summary>Every event, in order, from nothing.</summary>
    public static ConversationState Replay(IEnumerable<AgentEvent> events) =>
        events.Aggregate(ConversationState.Empty, Apply);

    public static ConversationState Apply(ConversationState state, AgentEvent e)
    {
        var next = e switch
        {
            SessionStateChanged s => OnSessionState(state, s),
            SessionConfigured c => state with
            {
                Model = c.Model ?? state.Model,
                Mode = c.Mode ?? state.Mode,
                Effort = c.Effort ?? state.Effort,
                ResumeToken = c.ResumeToken ?? state.ResumeToken,
                Account = c.Account ?? state.Account,
                ChosenModel = MovesAccount(state, c) ? null : state.ChosenModel,
            },
            SessionModelChosen m => state with { ChosenModel = m.Model },
            SessionOptionsReported o => state with { Options = o },
            TurnStarted t => state with
            {
                ActiveTurnId = t.TurnId ?? $"turn-{state.EntryCounter + 1}",
                EntryCounter = state.EntryCounter + 1,
                SessionState = AgentSessionState.Running,
            },
            TurnCompleted t => OnTurnCompleted(state, t),
            UserMessageAdded m => Append(state,
                new MessageEntry(m.MessageId, MessageRole.User, m.Text, false, m.Images) { TurnId = m.TurnId, At = m.At }),
            AssistantTextDelta d => OnAssistantDelta(state, d),
            AssistantMessageCompleted m => OnAssistantCompleted(state, m),
            ReasoningDelta r => OnReasoning(state, r),
            ToolStarted t => OnToolStarted(state, t),
            ToolUpdated u => UpdateTool(state, u.ToolCallId, tool => tool with
            {
                Title = u.Title ?? tool.Title,
                Detail = tool.Detail.MergedWith(u.Detail),
                Output = u.OutputDelta is null ? tool.Output : tool.Output + u.OutputDelta,
            }),
            ToolCompleted c => UpdateTool(state, c.ToolCallId, tool => tool with
            {
                Detail = tool.Detail.MergedWith(c.Detail),
                Output = c.Output ?? tool.Output,
                State = c.Status switch
                {
                    ToolStatus.Failed => ToolCallState.Failed,
                    ToolStatus.Declined => ToolCallState.Declined,
                    _ => ToolCallState.Completed,
                },
                CompletedAt = c.At,
            }),
            ApprovalRequested a => state with
            {
                PendingApprovals = state.PendingApprovals.RemoveAll(p => p.RequestId == a.RequestId).Add(a),
            },
            ApprovalResolved a => OnApprovalResolved(state, a),
            QuestionsAsked q => state with
            {
                PendingQuestions = state.PendingQuestions.RemoveAll(p => p.RequestId == q.RequestId).Add(q),
            },
            QuestionsAnswered q => OnQuestionsAnswered(state, q),
            PlanUpdated p => state with { Plan = p },
            PlanProposed p => Append(Numbered(state, out var planId),
                new ProposedPlanEntry(planId, p.Markdown) { TurnId = p.TurnId, At = p.At }),
            UsageUpdated u => state with { Usage = Merge(state.Usage, u.Usage) },
            CheckpointCaptured c => OnCheckpoint(state, c),
            CheckpointRestored r => OnRestored(state, r),
            NoticeRaised n => Append(Numbered(state, out var noticeId),
                new NoticeEntry(noticeId, n.Level, n.Text) { TurnId = n.TurnId, At = n.At }),
            _ => state,
        };

        return next with { LastSequence = Math.Max(state.LastSequence, e.Sequence) };
    }

    private static ConversationState OnSessionState(ConversationState state, SessionStateChanged s)
    {
        var next = state with { SessionState = s.State };
        if (s.State is not (AgentSessionState.Stopped or AgentSessionState.Failed)) return next;

        next = CloseOpenWork(next with { ActiveTurnId = null });
        return s.State == AgentSessionState.Failed && s.Detail is { Length: > 0 } detail
            ? Append(Numbered(next, out var id), new NoticeEntry(id, NoticeLevel.Error, detail) { At = s.At })
            : next;
    }

    private static ConversationState OnTurnCompleted(ConversationState state, TurnCompleted t)
    {
        var next = CloseOpenWork(state with
        {
            ActiveTurnId = null,
            SessionState = state.SessionState == AgentSessionState.Running
                ? AgentSessionState.Ready
                : state.SessionState,
        });

        var text = t.Outcome switch
        {
            TurnOutcome.Interrupted => "Interrupted.",
            TurnOutcome.Failed => string.IsNullOrWhiteSpace(t.Error) ? "The turn failed." : t.Error,
            _ => null,
        };
        if (text is null) return next;

        var level = t.Outcome == TurnOutcome.Failed ? NoticeLevel.Error : NoticeLevel.Info;
        return Append(Numbered(next, out var id), new NoticeEntry(id, level, text) { TurnId = t.TurnId, At = t.At });
    }

    /// <summary>What a turn or a session leaves behind when it ends, closed.</summary>
    private static ConversationState CloseOpenWork(ConversationState state)
    {
        var timeline = state.Timeline;
        for (var i = 0; i < timeline.Count; i++)
        {
            switch (timeline[i])
            {
                case MessageEntry { IsStreaming: true } message:
                    timeline = timeline.SetItem(i, message with { IsStreaming = false });
                    break;
                case WorkGroupEntry group when group.Items.Any(item => item is ToolCallItem { State: ToolCallState.Running }):
                    timeline = timeline.SetItem(i, group with
                    {
                        Items = [.. group.Items.Select(item => item is ToolCallItem { State: ToolCallState.Running } tool
                            ? tool with { State = ToolCallState.Abandoned }
                            : item)],
                    });
                    break;
            }
        }

        return state with { Timeline = timeline, PendingApprovals = [], PendingQuestions = [] };
    }

    private static ConversationState OnAssistantDelta(ConversationState state, AssistantTextDelta d)
    {
        if (d.Delta.Length == 0) return state;
        var index = IndexOf(state.Timeline, d.MessageId);
        if (index >= 0 && state.Timeline[index] is MessageEntry existing)
            return state with
            {
                Timeline = state.Timeline.SetItem(index, existing with { Text = existing.Text + d.Delta, IsStreaming = true }),
            };

        return Append(state,
            new MessageEntry(d.MessageId, MessageRole.Assistant, d.Delta, true, []) { TurnId = d.TurnId, At = d.At });
    }

    private static ConversationState OnAssistantCompleted(ConversationState state, AssistantMessageCompleted m)
    {
        var index = IndexOf(state.Timeline, m.MessageId);
        if (index >= 0 && state.Timeline[index] is MessageEntry existing)
        {
            // An agent that streamed and then says it had nothing keeps what it streamed.
            var text = m.Text.Length > 0 ? m.Text : existing.Text;
            return state with
            {
                Timeline = state.Timeline.SetItem(index, existing with { Text = text, IsStreaming = false }),
            };
        }

        return string.IsNullOrWhiteSpace(m.Text)
            ? state
            : Append(state,
                new MessageEntry(m.MessageId, MessageRole.Assistant, m.Text, false, []) { TurnId = m.TurnId, At = m.At });
    }

    private static ConversationState OnReasoning(ConversationState state, ReasoningDelta r)
    {
        if (r.Delta.Length == 0) return state;
        var (next, groupIndex) = CurrentGroup(state, r);
        var group = (WorkGroupEntry)next.Timeline[groupIndex];
        var itemIndex = group.Items.FindIndex(item => item.Id == r.MessageId);

        var items = itemIndex >= 0 && group.Items[itemIndex] is ReasoningItem thought
            ? group.Items.SetItem(itemIndex, thought with { Text = thought.Text + r.Delta })
            : group.Items.Add(new ReasoningItem(r.MessageId, r.Delta));

        return next with { Timeline = next.Timeline.SetItem(groupIndex, group with { Items = items }) };
    }

    private static ConversationState OnToolStarted(ConversationState state, ToolStarted t)
    {
        // A repeated start is an agent re-announcing a call it already made; it refreshes, never doubles.
        if (FindTool(state.Timeline, t.ToolCallId) is not null)
            return UpdateTool(state, t.ToolCallId, tool => tool with
            {
                Kind = t.Kind,
                Title = t.Title,
                Detail = tool.Detail.MergedWith(t.Detail),
            });

        var (next, groupIndex) = CurrentGroup(state, t);
        var group = (WorkGroupEntry)next.Timeline[groupIndex];
        var tool = new ToolCallItem(t.ToolCallId, t.Kind, t.Name, t.Title, t.Detail, "", ToolCallState.Running, t.At, null);
        return next with { Timeline = next.Timeline.SetItem(groupIndex, group with { Items = group.Items.Add(tool) }) };
    }

    private static ConversationState OnApprovalResolved(ConversationState state, ApprovalResolved a)
    {
        var request = state.PendingApprovals.FirstOrDefault(p => p.RequestId == a.RequestId);
        if (request is null) return state;

        var next = state with { PendingApprovals = state.PendingApprovals.Remove(request) };
        var decision = new DecisionItem(a.RequestId, request.Title, a.Decision);

        // Beside the tool it was about, where that tool is known; otherwise where the work is now.
        if (request.ToolCallId is { } toolCallId && FindTool(next.Timeline, toolCallId) is (var groupAt, _))
        {
            var owner = (WorkGroupEntry)next.Timeline[groupAt];
            return next with { Timeline = next.Timeline.SetItem(groupAt, owner with { Items = owner.Items.Add(decision) }) };
        }

        var (withGroup, groupIndex) = CurrentGroup(next, a);
        var group = (WorkGroupEntry)withGroup.Timeline[groupIndex];
        return withGroup with
        {
            Timeline = withGroup.Timeline.SetItem(groupIndex, group with { Items = group.Items.Add(decision) }),
        };
    }

    private static ConversationState OnQuestionsAnswered(ConversationState state, QuestionsAnswered q)
    {
        var round = state.PendingQuestions.FirstOrDefault(p => p.RequestId == q.RequestId);
        if (round is null) return state;

        return Append(state with { PendingQuestions = state.PendingQuestions.Remove(round) },
            new QuestionsEntry(q.RequestId, round.Questions, q.Answers) { TurnId = round.TurnId, At = q.At });
    }

    private static ConversationState OnCheckpoint(ConversationState state, CheckpointCaptured c)
    {
        var next = state with { LatestCheckpointId = c.CheckpointId };
        return c.Files.Count == 0 || c.BaseCheckpointId is null
            ? next
            : Append(next,
                new CheckpointEntry(c.CheckpointId, c.BaseCheckpointId, c.Files, false) { TurnId = c.TurnId, At = c.At });
    }

    /// <summary>
    /// Marks the turn whose start was restored and every turn after it, and says so where the conversation
    /// is now.
    /// </summary>
    /// <remarks>A later turn's changes were reverted along with it, and its base is a tree that no longer
    /// exists — offered Undo, it would put back the very changes the user just took out.</remarks>
    private static ConversationState OnRestored(ConversationState state, CheckpointRestored r)
    {
        var timeline = state.Timeline;
        var reverted = false;
        for (var i = 0; i < timeline.Count; i++)
        {
            if (timeline[i] is not CheckpointEntry checkpoint) continue;
            reverted |= checkpoint.BaseCheckpointId == r.CheckpointId;
            if (reverted) timeline = timeline.SetItem(i, checkpoint with { Restored = true });
        }

        return Append(Numbered(state with { Timeline = timeline }, out var id),
            new NoticeEntry(id, NoticeLevel.Info, "Files were restored to how they were before that turn.") { At = r.At });
    }

    private static TokenUsage Merge(TokenUsage? old, TokenUsage newer) =>
        old is null
            ? newer
            : new TokenUsage(
                newer.UsedTokens ?? old.UsedTokens,
                newer.ContextWindow ?? old.ContextWindow,
                newer.InputTokens ?? old.InputTokens,
                newer.OutputTokens ?? old.OutputTokens,
                newer.CostUsd ?? old.CostUsd);

    private static ConversationState UpdateTool(ConversationState state, string toolCallId,
        Func<ToolCallItem, ToolCallItem> change)
    {
        if (FindTool(state.Timeline, toolCallId) is not (var groupIndex, var itemIndex)) return state;

        var group = (WorkGroupEntry)state.Timeline[groupIndex];
        var tool = (ToolCallItem)group.Items[itemIndex];
        return state with
        {
            Timeline = state.Timeline.SetItem(groupIndex, group with { Items = group.Items.SetItem(itemIndex, change(tool)) }),
        };
    }

    /// <summary>The group the timeline ends with, or a new one appended for this event.</summary>
    private static (ConversationState State, int GroupIndex) CurrentGroup(ConversationState state, AgentEvent e)
    {
        if (state.Timeline.Count > 0 && state.Timeline[^1] is WorkGroupEntry)
            return (state, state.Timeline.Count - 1);

        var next = Append(Numbered(state, out var id), new WorkGroupEntry(id, []) { TurnId = e.TurnId, At = e.At });
        return (next, next.Timeline.Count - 1);
    }

    /// <summary>Where a tool call is, searching from the newest group, since that is nearly always it.</summary>
    private static (int GroupIndex, int ItemIndex)? FindTool(ImmutableList<TimelineEntry> timeline, string toolCallId)
    {
        for (var i = timeline.Count - 1; i >= 0; i--)
        {
            if (timeline[i] is not WorkGroupEntry group) continue;
            var itemIndex = group.Items.FindIndex(item => item is ToolCallItem && item.Id == toolCallId);
            if (itemIndex >= 0) return (i, itemIndex);
        }

        return null;
    }

    private static int IndexOf(ImmutableList<TimelineEntry> timeline, string id)
    {
        for (var i = timeline.Count - 1; i >= 0; i--)
            if (timeline[i].Id == id) return i;
        return -1;
    }

    /// <summary>Whether a session starts a stretch on another account than the one running until now.</summary>
    /// <remarks>A model chosen there was spelled for that account's provider, so it goes with it.</remarks>
    private static bool MovesAccount(ConversationState state, SessionConfigured configured) =>
        configured.Account is { } account && state.Account is not null && !account.IsSameAs(state.Account);

    /// <summary>Adds an entry, stamped with the account the conversation is running as.</summary>
    /// <remarks>The one place every timeline entry is made, which is what makes the stamp a rule rather than
    /// something each branch has to remember. An entry that carries one already keeps it — nothing does yet,
    /// and a later replay of somebody else's segment must not be re-attributed to whoever is running now.
    /// </remarks>
    private static ConversationState Append(ConversationState state, TimelineEntry entry) =>
        state with { Timeline = state.Timeline.Add(entry.Account is null ? entry with { Account = state.Account } : entry) };

    private static ConversationState Numbered(ConversationState state, out string id)
    {
        id = $"entry-{state.EntryCounter + 1}";
        return state with { EntryCounter = state.EntryCounter + 1 };
    }
}
