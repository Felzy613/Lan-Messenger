# FileBubble

A document attachment: icon, name, size and time, with Open and Show pill buttons.

## Use it
Any `__FILE__:` message that is not an image or video. Photos and videos use MediaBubble.

## You provide
The file name, size, time, status, and whether the file still exists on disk (check it off the main thread).

## Rules
- The document icon is 28px in `accent-ink` (`accent-ink-out` in an outgoing bubble). The name is `file-name`, clamped to two lines.
- **Open** is `lm-pill--accent` and **Show** (Show in Finder or Show in Explorer) is a neutral pill. When the file is gone, both are replaced by "Deleted" in `ink-secondary`.
- Max width `size-file-bubble-max` (320). Padding 10/12.
- The bubble is draggable out to Finder or Explorer while the file exists.
