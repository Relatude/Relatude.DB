import { useEffect, useMemo, useState } from "react";
import { IconPlayerPlayFilled, IconX } from "@tabler/icons-react";
import { mediaUrl, type FileValueView } from "../server/query";
import { formatBytes } from "../format";

// The picture side of a file property: a thumbnail in the node form, and the whole thing in a dialog
// when it is clicked. Only what a browser can draw gets one - an image or a video; a document is
// still just its name and its size.
//
// Every variant is asked of the store rather than built here (a thumbnail is an image adjusted to a
// width, a video's thumbnail is a frame taken out of it), so what the form shows is exactly what the
// conversion engine serves the rest of the world. Those conversions are made on demand and the first
// request for one usually arrives before it is finished, which is why loading goes through useMedia
// at the bottom of this file rather than through a plain img src.

const thumbWidth = 400; // asked for at twice the tile, so the tile is sharp on a dense screen
const fullWidth = 1920;
const originalSizeLimit = 4 * 1024 * 1024; // under this an image is shown as it is, with no conversion
const pollIntervalMs = 1500;
const maxPolls = 80; // ~2 minutes; a first video conversion also waits for ffmpeg to be fetched

/** A file value in the node form: what it is, next to what it looks like. */
export function FilePreview({ storeId, file, compact }: { storeId: string; file: FileValueView; compact?: boolean }) {
  const [open, setOpen] = useState(false);
  const previewable = file.fileType === "Image" || file.fileType === "Video";
  return (
    <>
      <span className={"node-file" + (compact ? " compact" : "")}>
        {previewable && <MediaThumb storeId={storeId} file={file} onOpen={() => setOpen(true)} />}
        <span className="node-file-text">
          <strong>{file.name}</strong>
          {!compact && (
            <span className="muted">
              {formatBytes(file.size)} · {file.contentType}
              {file.width > 0 ? ` · ${file.width}×${file.height}` : ""}
            </span>
          )}
        </span>
      </span>
      {open && <MediaDialog storeId={storeId} file={file} onClose={() => setOpen(false)} />}
    </>
  );
}

/** The small tile. A vector image is shown as it is: nothing gains from resizing one. */
function MediaThumb({ storeId, file, onOpen }: { storeId: string; file: FileValueView; onOpen: () => void }) {
  const url = useMemo(
    () => (file.format === "Svg" ? mediaUrl(storeId, file) : mediaUrl(storeId, file, { width: thumbWidth })),
    [storeId, file],
  );
  const { src, converting, error } = useMedia(url);
  return (
    <button className="media-thumb" onClick={onOpen} disabled={!!error} title={error ?? "Show " + file.name}>
      {src && !converting && <img src={src} alt={file.name} />}
      {converting && <span className="media-thumb-note">converting…</span>}
      {!src && !converting && !error && <span className="media-thumb-note">loading…</span>}
      {error && <span className="media-thumb-note">{error}</span>}
      {file.fileType === "Video" && src && !converting && (
        <span className="media-thumb-play">
          <IconPlayerPlayFilled size={16} />
        </span>
      )}
    </button>
  );
}

/** The whole file, as big as the window allows. Closes on the backdrop, on Escape and on the button. */
function MediaDialog({ storeId, file, onClose }: { storeId: string; file: FileValueView; onClose: () => void }) {
  const original = useMemo(() => mediaUrl(storeId, file), [storeId, file]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="dialog-backdrop media-backdrop" onClick={onClose}>
      <div className="media-dialog" onClick={(e) => e.stopPropagation()}>
        <div className="media-dialog-head">
          <strong>{file.name}</strong>
          <span className="muted">
            {formatBytes(file.size)} · {file.contentType}
            {file.width > 0 ? ` · ${file.width}×${file.height}` : ""}
          </span>
          <div className="query-spacer" />
          <a className="action-button" href={original} target="_blank" rel="noreferrer">
            Open original
          </a>
          <button className="icon-button" title="Close" onClick={onClose}>
            <IconX size={16} stroke={1.8} />
          </button>
        </div>
        <div className="media-dialog-body">
          {file.fileType === "Video" ? (
            // the file itself, not a conversion of it: the browser plays it, and the range requests
            // it makes to seek are served from the stored file
            <video className="media-full" src={original} controls autoPlay />
          ) : (
            <FullImage storeId={storeId} file={file} />
          )}
        </div>
      </div>
    </div>
  );
}

function FullImage({ storeId, file }: { storeId: string; file: FileValueView }) {
  const url = useMemo(() => {
    // A picture a browser draws well enough on its own is shown untouched: no conversion to wait
    // for, nothing lost, and an animated gif keeps moving. Only the big ones are resized, to a
    // width that still fills a screen.
    const asIs = file.format === "Svg" || file.format === "Gif" || file.size <= originalSizeLimit;
    return asIs ? mediaUrl(storeId, file) : mediaUrl(storeId, file, { width: Math.min(fullWidth, file.width || fullWidth) });
  }, [storeId, file]);
  const { src, converting, error } = useMedia(url);
  if (error) return <span className="query-error">{error}</span>;
  if (converting) return <span className="muted">Converting…</span>;
  if (!src) return <span className="muted">Loading…</span>;
  return <img className="media-full" src={src} alt={file.name} />;
}

interface MediaState {
  src: string | null;
  converting: boolean;
  error: string | null;
}

/**
 * Loads one media url, told apart from a plain img src by two things: it can say what went wrong,
 * and it knows the difference between a picture and a picture of a conversion in progress. A variant
 * that is not converted yet answers with the engine's status image and `X-Relatude-Ready: 0`; the
 * only thing to do about that is to ask again, which this does until the real one arrives.
 *
 * Asking has an end to it. A conversion nothing can finish - a format no converter reads, a
 * converter that failed - would otherwise leave the form saying "converting…" for good, so once the
 * asking stops the status image is shown after all: it is the store's own account of what happened,
 * and it reads clearly enough in the dialog.
 */
function useMedia(url: string): MediaState {
  const [state, setState] = useState<MediaState>({ src: null, converting: false, error: null });
  useEffect(() => {
    const abort = new AbortController();
    let objectUrl: string | null = null;
    let attempts = 0;
    let timer: number | undefined;
    const show = (blob: Blob, converting: boolean) => {
      const next = URL.createObjectURL(blob);
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = next;
      setState({ src: next, converting, error: null });
    };
    const attempt = async () => {
      try {
        const response = await fetch(url, { signal: abort.signal });
        if (!response.ok) throw new Error(await errorOf(response));
        const ready = response.headers.get("x-relatude-ready") !== "0";
        const blob = await response.blob();
        if (abort.signal.aborted) return;
        const again = !ready && attempts++ < maxPolls;
        show(blob, again);
        if (again) timer = window.setTimeout(attempt, pollIntervalMs);
      } catch (error) {
        if (abort.signal.aborted) return;
        setState({ src: null, converting: false, error: error instanceof Error ? error.message : String(error) });
      }
    };
    attempt();
    return () => {
      window.clearTimeout(timer);
      abort.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [url]);
  return state;
}

async function errorOf(response: Response): Promise<string> {
  try {
    const body = await response.json();
    if (typeof body?.error === "string") return body.error;
  } catch {
    // not json, so the status is all there is to say
  }
  return `The file could not be read (HTTP ${response.status}).`;
}
