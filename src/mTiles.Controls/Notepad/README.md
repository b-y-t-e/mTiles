# Notepad.Avalonia, vendored

`MarkdownViewer` and `NoteEditor`, taken from
[b-y-t-e/Notepad.Avalonia](https://github.com/b-y-t-e/Notepad.Avalonia) (MIT, same author as this
application) at version **0.3.1** and built from source here instead of through the NuGet package.

**Why the sources and not the package.** The Agent tile draws a turn's patch through `MarkdownViewer`,
because a viewer whose selection spans the whole document is the only thing that lets two lines of a
diff be dragged through and copied together — which one control per row cannot do, a selection
belonging to one `SelectableTextBlock` and nothing in Avalonia spanning siblings. What the package
could not do was colour it: 0.3.1 has no syntax highlighting at all, `CodeLanguage` is stored and never
read, so a patch came out monochrome. The colouring is a change to this control, and a change to a
control on the other side of a NuGet release is a change nobody makes while looking at the screen it is
for.

**What was changed here**, and it is the whole of the diff against 0.3.1:

- `MarkdownViewer.HighlightDiff` — **off unless asked for**, so every document that rendered one way
  before renders that way still. `GoalMarkdownView` is what asks.
- `DiffAddedBackground`/`DiffRemovedBackground` — a ground the whole width of the block, which is where
  a diff's colour belongs: on a block of twenty added lines, green *text* is a paragraph of green and
  the eye reads the colour instead of the code.
- `DiffAddedForeground`/`DiffRemovedForeground`/`DiffMetaForeground` — null leaves the block's own.
- `VisualLine.Band`, and the one rectangle in `RenderContent` that draws it.
- `IsDiffLanguage`/`DiffRoleOf`/`DiffRolesOf` — internal and pinned by `MarkdownDiffTests`, because
  they are rules about somebody else's text: which fences are patches, and which first characters are
  the format rather than the file. The block is read **as a whole**, tracking the hunk, and that is not
  fastidiousness: a line of two dashes removed from a file is written `---` and one of two pluses added
  to it `+++`, so read a line at a time the two loudest bands in the block are lost exactly where the
  patch is quoting somebody's code — and the pair that opens the next file is only recognisable from
  the line after it.

- `MarkdownParser` — **the body of a fenced block is content and nothing else**, which 0.3.1 did not
  quite hold to and which only a patch notices. Three rules for prose reached into it: the blockquote
  markers were stripped at whatever depth the line carried rather than at the depth the block was
  opened at (so a patch of a markdown file, a doctest or a heredoc lost the `>` at the head of a line,
  showing a line the file does not have); tabs were expanded for the whole document rather than only
  for the copy structural decisions are taken on (so a patch of a tab-indented file — Go, a Makefile —
  was copied out with the wrong indentation); and the pass that harvests link definitions tracked a
  fence by its first three characters (so a block opened with four backticks ended on the first body
  line carrying three, and every later line shaped like a link definition was blanked out of the
  patch). Pinned by `MarkdownDiffTests`.

**Carrying it back upstream** is the tidy end of this and has not been done. Until it is, a fix made in
the package does not arrive here and a fix made here does not arrive there — so a change to these files
is worth a moment's thought about which of the two it belongs to.
