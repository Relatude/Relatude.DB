import { IconMinus, IconX } from "@tabler/icons-react";

/**
 * The corner buttons of a dialog: the cross that closes it, and the minimize beside it where the
 * dialog is one that can be put away and left running.
 *
 * Every dialog in the UI carries these, so closing one is the same gesture wherever you meet it -
 * the same gesture as closing a window, which is what a person reaches for before they read the
 * buttons at the bottom. They sit in the title row and push themselves to its right end.
 */
export function DialogTools({ onMinimize, onClose, closeTitle }: { onMinimize?: () => void; onClose: () => void; closeTitle?: string }) {
  return (
    <span className="dialog-tools">
      {onMinimize && (
        <button className="icon-button" onClick={onMinimize} title="Keep it running and carry on - it goes to the top bar" aria-label="Minimize">
          <IconMinus size={15} stroke={2} />
        </button>
      )}
      <button className="icon-button" onClick={onClose} title={closeTitle ?? "Close"} aria-label={closeTitle ?? "Close"}>
        <IconX size={15} stroke={2} />
      </button>
    </span>
  );
}
