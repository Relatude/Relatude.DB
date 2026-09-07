import { useEffect, useRef, useState, type DragEvent, type MouseEvent as ReactMouseEvent, type RefObject } from "react";
import { IconFile, IconFileText, IconFileTypePdf, IconMovie, IconMusic, IconPhoto, IconPlayerPlayFilled } from "@tabler/icons-react";
import { fileUrl, thumbUrl, type FileInfo } from "../server/files";
import { fileKind } from "../code/language";
import { formatBytes } from "../format";

// One file in the thumbnail view of the files section. What it shows depends on what the file is:
//
//  - an image: a copy scaled down on the server (see the "thumb" endpoint), so a folder of
//    photographs costs kilobytes a tile rather than the originals;
//  - a video: the file itself, asked of the browser at a moment a little into it, which makes the
//    player draw that frame. Only the metadata and the bytes around that point are fetched, and only
//    for the tiles being looked at;
//  - anything else, and anything the server would not make a thumbnail of: the file type's icon.
//
// Nothing is asked for until the tile is near the screen, and once asked it stays: scrolling back up
// finds the pictures already there rather than fetching them again.

const thumbRequestWidth = 320; // twice the tile, so it stays sharp on a dense screen
const videoFrameAt = 1; // seconds into the video; the very first frame is often black

export function FileTile(p: {
  ioId: string;
  file: FileInfo;
  name: string; // the display name, which may be a path when the list reaches into subfolders
  scroller: RefObject<HTMLElement | null>; // the grid, which is what the tiles scroll inside
  selected: boolean;
  viewing: boolean;
  onClick: (e: ReactMouseEvent) => void;
  onToggle: () => void;
  onDragStart: (e: DragEvent) => void;
}) {
  const tile = useRef<HTMLDivElement>(null);
  const near = useInView(tile, p.scroller);
  const kind = fileKind(fileNameOf(p.file.key));
  // the server has nothing to show for this one (a format no converter reads, a file it could not
  // open): the icon is what is left, and it is what a tile without a preview shows anyway
  const [failed, setFailed] = useState(false);
  const showPicture = near && !failed && (kind === "image" || kind === "video");
  return (
    <div
      ref={tile}
      data-file-row=""
      className={"file-tile" + (p.selected ? " selected" : "") + (p.viewing ? " viewing" : "")}
      draggable
      onDragStart={p.onDragStart}
      onClick={p.onClick}
      title={p.file.key}
    >
      <div className={"file-tile-frame" + (showPicture && kind === "video" ? " video" : "")}>
        {showPicture && kind === "image" && (
          <img className="file-tile-image" src={thumbUrl(p.ioId, p.file.key, thumbRequestWidth, p.file.lastModifiedUtc)} alt="" onError={() => setFailed(true)} />
        )}
        {showPicture && kind === "video" && (
          <>
            <video
              className="file-tile-image"
              src={`${fileUrl(p.ioId, p.file.key, p.file.lastModifiedUtc)}#t=${videoFrameAt}`}
              preload="metadata"
              muted
              playsInline
              onError={() => setFailed(true)}
            />
            <span className="file-tile-play">
              <IconPlayerPlayFilled size={14} />
            </span>
          </>
        )}
        {!showPicture && <KindIcon kind={kind} />}
        <input
          type="checkbox"
          className="file-tile-check"
          checked={p.selected}
          onChange={p.onToggle}
          onClick={(e) => e.stopPropagation()}
          title="Select this file"
        />
        {(p.file.readers > 0 || p.file.writers > 0) && (
          <span className="badge file-tile-badge" title={`${p.file.readers} readers, ${p.file.writers} writers`}>
            in use
          </span>
        )}
      </div>
      <span className="file-tile-name">{p.name}</span>
      <span className="file-tile-size muted">{formatBytes(p.file.size)}</span>
    </div>
  );
}

function KindIcon({ kind }: { kind: ReturnType<typeof fileKind> }) {
  const size = 30;
  const stroke = 1.3;
  switch (kind) {
    case "image":
      return <IconPhoto size={size} stroke={stroke} />;
    case "video":
      return <IconMovie size={size} stroke={stroke} />;
    case "audio":
      return <IconMusic size={size} stroke={stroke} />;
    case "pdf":
      return <IconFileTypePdf size={size} stroke={stroke} />;
    case "text":
      return <IconFileText size={size} stroke={stroke} />;
    default:
      return <IconFile size={size} stroke={stroke} />;
  }
}

/**
 * Whether the element has come within reach of the scroller, and true from then on. That is what
 * keeps a folder of thousands of pictures to the cost of a screenful: nothing below the fold is
 * requested until it is scrolled towards, and nothing already fetched is thrown away and asked for
 * again on the way back up. A browser without IntersectionObserver simply loads everything.
 */
function useInView(element: RefObject<HTMLElement | null>, scroller: RefObject<HTMLElement | null>): boolean {
  const [seen, setSeen] = useState(false);
  useEffect(() => {
    if (seen) return;
    const node = element.current;
    if (!node) return;
    if (typeof IntersectionObserver === "undefined") {
      setSeen(true);
      return;
    }
    // a screenful either way, so a picture is normally there by the time it is scrolled to
    const observer = new IntersectionObserver((entries) => entries.some((entry) => entry.isIntersecting) && setSeen(true), {
      root: scroller.current,
      rootMargin: "400px 0px",
    });
    observer.observe(node);
    return () => observer.disconnect();
  }, [element, scroller, seen]);
  return seen;
}

function fileNameOf(key: string): string {
  const i = key.lastIndexOf("/");
  return i < 0 ? key : key.slice(i + 1);
}
