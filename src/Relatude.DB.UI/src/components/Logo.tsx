// The node glyph alone (the circle with its connector line), used when the menu is collapsed.
// Same geometry as the start of the full wordmark, but drawn with strokes so it can be a
// touch thinner than the filled original when scaled up to icon size.
export function LogoMark({ height = 13 }: { height?: number | string }) {
  return (
    <svg viewBox="34 55 32 17" height={height} aria-label="Relatude">
      <circle cx="42.5" cy="63.5" r="5.8" fill="none" stroke="currentColor" strokeWidth="2" />
      <line x1="48.7" y1="63.7" x2="64.2" y2="63.7" stroke="currentColor" strokeWidth="2" />
    </svg>
  );
}

// The Relatude wordmark, copied from Relatude.DB.ServerUI (sections/main/logoBig.tsx).
// Renders in currentColor so it follows the theme. The blinking cursor is driven by the
// .logo-cursor CSS animation (app.css) with the same rhythm as the original SMIL chain.
export function Logo({ height = 40 }: { height?: number | string }) {
  return (
    // the artwork spans x 35..376, y 27..112; the original 55..355 viewBox clipped the left node glyph and the cursor
    <svg viewBox="33 24 346 90" height={height} fill="currentColor" aria-label="Relatude">
      <path d="M99.3,56.2c0,4.5-2.8,7.9-6.9,8.7l9.9,19.4h-3.3l-9.8-19.2H76.2v19.2h-3V42.5h17.1c5.1,0,8.9,4.1,8.9,9.3V56.2z M96.3,51.6
        c0-3.9-2.5-6.3-6.4-6.3H76.2v17h13.6c3.9,0,6.4-2.4,6.4-6.3V51.6z" />
      <polygon points="110.8,84.3 110.8,42.5 134.3,42.5 134.3,45.3 113.7,45.3 113.7,62 131.4,62 131.4,64.8 113.7,64.8 113.7,81.4
        134.8,81.4 134.8,84.3 " />
      <polygon points="142.9,84.3 142.9,42.5 145.8,42.5 145.8,81.4 165.9,81.4 165.9,84.3 " />
      <polygon points="222.6,45.3 222.6,84.3 219.6,84.3 219.6,45.3 207,45.3 207,42.5 235.2,42.5 235.2,45.3 " />
      <path d="M241.8,42.5h3v28.7c0,8.2,5.5,11,11.3,11c5.8,0,11.3-2.8,11.3-11V42.5h3v29.4c0,9.2-6.1,13.1-14.3,13.1
        c-8.2,0-14.3-4-14.3-13.1V42.5z" />
      <path d="M308.5,75c0,5.1-3.8,9.3-8.9,9.3h-18.4V42.5h18.4c5.1,0,8.9,4.1,8.9,9.3V75z M305.5,51.9c0-3.9-2.9-6.6-6.8-6.6h-14.5v36.1
        h14.6c3.9,0,6.7-2.7,6.7-6.6V51.9z" />
      <polygon points="318.9,84.3 318.9,42.5 342.4,42.5 342.4,45.3 321.9,45.3 321.9,62 339.5,62 339.5,64.8 321.9,64.8 321.9,81.4
        342.9,81.4 342.9,84.3 " />
      <path d="M210.2,112.3c-4.8,0-8.8-3.9-8.8-8.8c0-4.8,3.9-8.8,8.8-8.8c4.8,0,8.8,3.9,8.8,8.8C219,108.4,215.1,112.3,210.2,112.3
        M210.2,97.2c-3.5,0-6.3,2.8-6.3,6.3c0,3.5,2.8,6.3,6.3,6.3c3.5,0,6.3-2.8,6.3-6.3C216.5,100,213.7,97.2,210.2,97.2" />
      <polygon points="185.4,26.8 182.2,26.8 196.7,68.4 180,68.4 184.8,54.5 182,53.6 171.4,84.3 174.5,84.3 179,71.3 197.8,71.3
        206.5,96.7 209.6,96.7 " />
      <rect className="logo-cursor" x="352.1" y="81.3" width="23.9" height="2.9" />
      <path d="M42.5,70.7c-4,0-7.2-3.2-7.2-7.2c0-4,3.2-7.2,7.2-7.2s7.2,3.2,7.2,7.2C49.7,67.5,46.5,70.7,42.5,70.7 M42.5,59.1
        c-2.4,0-4.4,2-4.4,4.4c0,2.4,2,4.4,4.4,4.4c2.4,0,4.4-2,4.4-4.4C46.8,61.1,44.9,59.1,42.5,59.1" />
      <rect x="48.7" y="62.3" width="15.5" height="2.8" />
    </svg>
  );
}
