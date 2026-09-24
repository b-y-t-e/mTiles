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
/// for a request no process is waiting on. <b>A background sub-agent is the one exception</b>: it outlives the
/// turn that launched it, so the end of that turn leaves it — and what it is asking — alone, and only its own
/// end or the session's closes it.</item>
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
            HandoverRecorded h => OnHandover(state, h),
            SubAgentStarted s => OnSubAgentStarted(state, s),
            SubAgentProgressed p => UpdateSubAgent(state, p.SubAgentId,
                run => run.IsWorking ? run with { Progress = p.Progress } : run),
            SubAgentEnded s => OnSubAgentEnded(state, s),
            _ => state,
        };

        return next with { LastSequence = Math.Max(state.LastSequence, e.Sequence) };
    }

    private static ConversationState OnSessionState(ConversationState state, SessionStateChanged s)
    {
        var next = state with { SessionState = s.State };
        if (s.State is not (AgentSessionState.Stopped or AgentSessionState.Failed)) return next;

        // Every sub-agent went with the process, background or not.
        next = CloseOpenWork(StopSubAgents(next with { ActiveTurnId = null }, _ => true, s.At), sessionEnded: true);
        return s.State == AgentSessionState.Failed && s.Detail is { Length: > 0 } detail
            ? Append(Numbered(next, out var id), new NoticeEntry(id, NoticeLevel.Error, detail) { At = s.At })
            : next;
    }

    private static ConversationState OnTurnCompleted(ConversationState state, TurnCompleted t)
    {
        // A foreground sub-agent cannot outlive its turn, whatever it failed to say about ending; a background
        // one is exactly the one that does, and its approvals stay with it.
        var next = CloseOpenWork(StopSubAgents(state with
        {
            ActiveTurnId = null,
            SessionState = state.SessionState == AgentSessionState.Running
                ? AgentSessionState.Ready
                : state.SessionState,
        }, run => !run.Background, t.At), sessionEnded: false);

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
    /// <remarks>Except, at a turn's end, what a sub-agent is asking: that request is not the turn's but the
    /// sub-agent's, the session keeps waiting on it, and dropped from the screen it would wait for ever. The
    /// rule is the session's own (<c>PendingReplies.AbandonTurn</c>) — a request marked as a sub-agent's stays
    /// — so a sub-agent not announced yet keeps its request too; only one known to have ended loses it, and
    /// the session's end drops everything.</remarks>
    private static ConversationState CloseOpenWork(ConversationState state, bool sessionEnded)
    {
        bool OutlivesTheTurn(string? subAgentId) =>
            !sessionEnded && subAgentId is not null
                          && !state.SubAgents.Any(s => s.Id == subAgentId && !s.IsWorking);

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

        return state with
        {
            Timeline = timeline,
            PendingApprovals = state.PendingApprovals.RemoveAll(a => !OutlivesTheTurn(a.SubAgentId)),
            PendingQuestions = state.PendingQuestions.RemoveAll(q => !OutlivesTheTurn(q.SubAgentId)),
        };
    }

    /// <summary>A sub-agent began working, or began again.</summary>
    /// <remarks>A start for one already known is the same sub-agent woken up — codex keeps a finished one
    /// and can hand it more work — so it is set working again rather than listed twice, and keeps whatever
    /// the new start does not say.</remarks>
    private static ConversationState OnSubAgentStarted(ConversationState state, SubAgentStarted s)
    {
        if (state.SubAgents.Any(run => run.Id == s.SubAgentId))
            return UpdateSubAgent(state, s.SubAgentId, run => run with
            {
                Title = s.Title.Length > 0 ? s.Title : run.Title,
                ToolCallId = s.ToolCallId ?? run.ToolCallId,
                Background = s.Background || run.Background,
                Status = SubAgentStatus.Working,
                Result = null,
                EndedAt = null,
                TurnId = s.TurnId ?? run.TurnId,
            });

        var started = new SubAgentRun(s.SubAgentId, s.Title, s.ToolCallId, s.Background, SubAgentStatus.Working,
            null, null, s.At, null) { TurnId = s.TurnId };
        return Mirror(state with { SubAgents = state.SubAgents.Add(started) }, started);
    }

    /// <summary>A sub-agent stopped, and whatever it alone was waiting on goes with it.</summary>
    private static ConversationState OnSubAgentEnded(ConversationState state, SubAgentEnded s)
    {
        var next = UpdateSubAgent(state, s.SubAgentId, run => run with
        {
            Status = s.Outcome switch
            {
                SubAgentOutcome.Completed => SubAgentStatus.Completed,
                SubAgentOutcome.Failed => SubAgentStatus.Failed,
                _ => SubAgentStatus.Stopped,
            },
            Progress = null,
            Result = s.Result ?? run.Result,
            EndedAt = run.EndedAt ?? s.At,
        });

        return next with
        {
            PendingApprovals = next.PendingApprovals.RemoveAll(a => a.SubAgentId == s.SubAgentId),
            PendingQuestions = next.PendingQuestions.RemoveAll(q => q.SubAgentId == s.SubAgentId),
        };
    }

    /// <summary>Every sub-agent still working that <paramref name="which"/> picks, stopped.</summary>
    private static ConversationState StopSubAgents(ConversationState state, Func<SubAgentRun, bool> which,
        DateTimeOffset at)
    {
        var next = state;
        foreach (var run in state.SubAgents.Where(run => run.IsWorking && which(run)))
            next = UpdateSubAgent(next, run.Id, r => r with { Status = SubAgentStatus.Stopped, Progress = null, EndedAt = at });
        return next;
    }

    /// <summary>Changes one sub-agent, and the row of the call that launched it along with it.</summary>
    private static ConversationState UpdateSubAgent(ConversationState state, string id, Func<SubAgentRun, SubAgentRun> change)
    {
        var index = state.SubAgents.FindIndex(run => run.Id == id);
        if (index < 0) return state;

        var changed = change(state.SubAgents[index]);
        return Mirror(state with { SubAgents = state.SubAgents.SetItem(index, changed) }, changed);
    }

    /// <summary>Puts a sub-agent's state on the row of the call that launched it, where there is one.</summary>
    private static ConversationState Mirror(ConversationState state, SubAgentRun run) =>
        run.ToolCallId is { } toolCallId
            ? UpdateTool(state, toolCallId, tool => tool with { SubAgent = run })
            : state;

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
        // A sub-agent can be announced before the call that launched it — codex names its child threads as they
        // come up — and the row takes it over when it arrives.
        var tool = new ToolCallItem(t.ToolCallId, t.Kind, t.Name, t.Title, t.Detail, "", ToolCallState.Running, t.At, null)
        {
            SubAgent = state.SubAgents.LastOrDefault(run => run.ToolCallId == t.ToolCallId),
        };
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

    /// <summary>
    /// The work moved to another agent or another login: the seam is written down, and everything that
    /// belonged to the stretch that is ending stops being true.
    /// </summary>
    /// <remarks>
    /// <para><b>The token above all.</b> It is the outgoing CLI's own handle, and handed to the agent
    /// arriving it is not merely useless — <c>codex resume &lt;unknown&gt;</c> opens an interactive picker
    /// that a launch waits on for ever, and <c>agy --conversation &lt;unknown&gt;</c> warns, starts a fresh
    /// conversation and exits 0, so the tile cannot tell a resumed session from a lost one.</para>
    /// <para>The chosen model goes with it for the reason <see cref="MovesAccount"/> already gives, and the
    /// plan goes because it was the outgoing agent's own account of its work: kept, it would be drawn as the
    /// new agent's to-do list before that agent has said anything at all. It is not lost — the brief carries
    /// it, which is what a handover is for.</para>
    /// </remarks>
    private static ConversationState OnHandover(ConversationState state, HandoverRecorded h) =>
        Append(
            Numbered(state, out var id) with { ResumeToken = null, ChosenModel = null, Plan = null },
            new HandoverEntry(id, h.From ?? state.Account, h.To, h.Brief) { TurnId = h.TurnId, At = h.At });

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
