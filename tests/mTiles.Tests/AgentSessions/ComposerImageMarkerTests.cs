using Xunit;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// An image stands where its marker stands: the composer puts <c>[Image #n]</c> at the caret, a chip and
/// its marker go together, and what is sent keeps the order the sentence gave the pictures.
/// </summary>
public class ComposerImageMarkerTests
{
    private static readonly ImageAttachment A = new("image/png", "QQ==", "a");
    private static readonly ImageAttachment B = new("image/png", "Qg==", "b");

    [Fact]
    public void Text_and_images_are_interleaved_in_the_order_the_markers_are_read()
    {
        var parts = ImageMarkers.Interleave("before [Image #2] middle [Image #1] after", [A, B]);

        Assert.Collection(parts,
            p => Assert.Equal("before [Image #2]", ((TurnPart.Text)p).Value),
            p => Assert.Same(B, ((TurnPart.Image)p).Attachment),
            p => Assert.Equal(" middle [Image #1]", ((TurnPart.Text)p).Value),
            p => Assert.Same(A, ((TurnPart.Image)p).Attachment),
            p => Assert.Equal(" after", ((TurnPart.Text)p).Value));
    }

    [Fact]
    public void An_image_no_marker_names_goes_after_the_text_as_it_always_did()
    {
        var parts = ImageMarkers.Interleave("no markers here", [A]);

        Assert.Collection(parts,
            p => Assert.IsType<TurnPart.Text>(p),
            p => Assert.Same(A, ((TurnPart.Image)p).Attachment));
    }

    [Fact]
    public void A_blank_message_is_still_one_text_block()
    {
        var parts = ImageMarkers.Interleave("", []);

        Assert.Equal("", ((TurnPart.Text)Assert.Single(parts)).Value);
    }

    [Fact]
    public void A_marker_naming_no_image_stays_words()
    {
        var parts = ImageMarkers.Interleave("see [Image #3]", [A]);

        Assert.Equal("see [Image #3]", ((TurnPart.Text)parts[0]).Value);
        Assert.Same(A, ((TurnPart.Image)parts[1]).Attachment);
    }

    [Fact]
    public void Attaching_puts_the_marker_at_the_caret()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings);
        vm.Draft = "it was like  and now";
        vm.DraftCaretIndex = "it was like ".Length;

        vm.AttachImage(A);

        Assert.Equal("it was like [Image #1] and now", vm.Draft);
        Assert.Single(vm.Attachments.Items);
    }

    [Fact]
    public void Removing_the_chip_takes_the_marker_and_deleting_the_marker_takes_the_chip()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings);
        vm.AttachImage(A);
        vm.AttachImage(B);

        vm.RemoveAttachmentCommand.Execute(vm.Attachments.Items[0]);
        Assert.DoesNotContain("[Image #1]", vm.Draft);
        Assert.Equal(2, Assert.Single(vm.Attachments.Items).Index);

        vm.Draft = vm.Draft.Replace("[Image #2]", "");
        Assert.Empty(vm.Attachments.Items);
    }

    [Fact]
    public void A_marker_broken_and_restored_by_hand_still_names_its_image()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings);
        vm.AttachImage(A);
        var draft = vm.Draft;

        vm.Draft = draft.Replace("]", "");
        Assert.Empty(vm.Attachments.Items);

        vm.Draft = draft;
        Assert.Equal(A, Assert.Single(vm.Attachments.Items).Image);
    }

    [Fact]
    public void An_attachment_is_context_and_never_the_goals_scope()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"mtiles-scope-{Guid.NewGuid():N}");

        Assert.True(AttachmentStore.IsContextOnly(".mtiles/attachments/20260919-log.txt", workspace));
        Assert.True(AttachmentStore.IsContextOnly(Path.Combine(Path.GetTempPath(), "elsewhere.txt"), workspace));
        Assert.False(AttachmentStore.IsContextOnly("src/Cart.cs", workspace));
    }

    [Fact]
    public void The_chips_follow_the_order_the_text_names_the_images_in()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings);
        vm.AttachImage(A);                  // #1
        vm.DraftCaretIndex = 0;
        vm.AttachImage(B);                  // #2, pasted in front of #1

        Assert.Equal([2, 1], vm.Attachments.Items.Select(image => image.Index));
    }

    [Fact]
    public void What_is_sent_is_renumbered_from_one_in_reading_order()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings);
        vm.AttachImage(A);                  // #1
        vm.AttachImage(B);                  // #2
        vm.Draft = "first [Image #2] then [Image #1] and [Image #7]";

        var (text, images) = vm.OutgoingMessage();

        Assert.Equal("first [Image #1] then [Image #2] and ", text);
        Assert.Equal([B, A], images);
    }

    [Fact]
    public async Task A_file_in_the_workspace_is_named_where_it_is_and_one_outside_it_is_copied_in()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mtiles-attach-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        try
        {
            File.WriteAllText(Path.Combine(workspace, "src", "a b.cs"), "x");
            Assert.Equal("@\"src/a b.cs\"",
                (await ComposerFileReference.ForAsync(Path.Combine(workspace, "src", "a b.cs"), workspace)).Mention);

            var outside = Path.Combine(root, "schema.sql");
            File.WriteAllText(outside, "create table t();");
            var (mention, notice) = await ComposerFileReference.ForAsync(outside, workspace);

            Assert.Null(notice);
            Assert.StartsWith("@.mtiles/attachments/", mention);
            Assert.EndsWith("-schema.sql", mention);
            Assert.Equal("create table t();", File.ReadAllText(Path.Combine(workspace, mention[1..])));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_chip_is_drawn_for_every_mention_of_something_that_exists_and_its_x_takes_the_mention()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"mtiles-chips-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        File.WriteAllText(Path.Combine(workspace, "log.txt"), "");
        try
        {
            const string text = "read @log.txt then ask @admin about @docs please";
            var files = new ComposerFileScanner(workspace).In(text);

            Assert.Collection(files,
                f => Assert.Equal("log.txt", f.Name),
                f => Assert.True(f.IsDirectory));
            Assert.Equal("read then ask @admin about @docs please", files[0].RemoveFrom(text));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static AgentConversationTileViewModel NewTile(TempSettings settings) =>
        ConversationTiles.New(settings);
}
