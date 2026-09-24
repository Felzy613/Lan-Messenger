# MediaBubble

A photo or video inline in the thread, with the file name, time and ticks in a footer below it.

## Rules
- The media sits 4px inside the bubble with `radius-media` (16 − 4, concentric). Previews are at most `size-media-max`.
- The time and ticks go in the footer, never over the picture. No tint can hold 3:1 for a read tick over an arbitrary photo.
- Video gets a 48px play disc and a duration pill, both on `scrim` glass with `ink-inverse`.
- A file that has moved or been deleted gets a placeholder row: its name, then "File no longer available".

## Build
Decode thumbnails keyed by path plus modification date and size, never path alone. A file overwritten in place must refresh. On Windows, set `BitmapCreateOptions.IgnoreImageCache` before `UriSource`.
