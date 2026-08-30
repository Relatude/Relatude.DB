import { memo, useState } from "react";

// The animated Relatude logo (network draw-in intro), copied from
// Relatude.DB.ServerUI (sections/main/logoBig.tsx, getLogoHtml). Injected as raw
// HTML because the animation lives in a <style> block inside the SVG itself.
// Meant for the login screen; render only one instance at a time (the SVG uses fixed ids).
//
// The intro is meant to be seen once per visit, which takes two things. React 19 re-applies
// dangerouslySetInnerHTML on every render without comparing it, so any state change on the
// login screen (a keystroke, the sign-in click) would re-inject the SVG and restart its CSS
// animations: memo keeps the component from re-rendering at all. And if the screen is mounted
// again later — after a logout, say — introPlayed skips the intro to its finished state.
let introPlayed = false;

export const AnimatedLogo = memo(function AnimatedLogo({
  color = "var(--accent)",
  speed = 0.75,
  height = "120px",
  padding = "0",
}: {
  color?: string;
  speed?: number;
  height?: string;
  padding?: string;
}) {
  const [replay] = useState(() => {
    const already = introPlayed;
    introPlayed = true;
    return already;
  });
  return (
    <div className={replay ? "logo-played" : undefined} dangerouslySetInnerHTML={{ __html: getLogoHtml(padding, color, speed, height) }} />
  );
});

const getLogoHtml = (padding: string, color: string, speed: number, height: string) => (`
    <svg id="relatude-anim" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 340.7 85.5" height="${height}" style="padding: ${padding};">
      <defs>
        <clipPath id="clip-rel1">
          <path d="M28.9,35.28H14.28a7.22,7.22,0,1,0,0,2.84H28.9ZM7.2,41.08a4.38,4.38,0,1,1,4.38-4.38A4.38,4.38,0,0,1,7.2,41.08Z" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel2">
          <path d="M66.37,56.76,57,38.05c4.2-.56,7-4.68,7-8.55V24.2a8.65,8.65,0,0,0-8.22-8.42H38V57.5h2.84V38.21l13-.08L63.83,58ZM40.82,18.62H55.7a5.85,5.85,0,0,1,5.38,5.58v5.3c0,2.79-2,5.78-5.09,5.78l-15.17.09Z" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel3">
          <polygon points="99 18.62 99 15.78 75.68 15.78 75.68 57.52 99 57.52 99 54.68 78.52 54.68 78.52 38.22 96 38.22 96 35.38 78.52 35.38 78.52 18.62 99 18.62" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel4">
          <polygon points="130.6 57.52 107.68 57.52 107.68 15.7 110.52 15.7 110.52 54.68 130.6 54.68 130.6 57.52" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel5">
          <path d="M175,67.68a8.45,8.45,0,0,0-1.43.13L149.74-.37l-2.68.94,14.27,40.81H144.76l4.68-13.72-2.68-.92-10.3,30.2,2.68.92,4.65-13.64h18.53l8.56,24.47a9,9,0,1,0,4.12-1Zm0,15.2a6.18,6.18,0,1,1,6.18-6.18A6.18,6.18,0,0,1,175,82.88Z" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel6">
          <polygon points="199.5 15.78 171.7 15.78 171.7 18.62 184.19 18.62 184.38 57.41 187.22 57.39 187.02 18.62 199.5 18.62 199.5 15.78" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel7">
          <path d="M220.74,58.32c-7,0-14.16-4.44-14.16-12.92V15.7h2.84V45.4a9.29,9.29,0,0,0,3.44,7.52,12.6,12.6,0,0,0,7.88,2.56c5.51,0,11.44-3.18,11.44-10.18V15.7H235V45.3C235,53.85,227.83,58.32,220.74,58.32Z" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel8">
          <path d="M264,57.52H245.88V15.78H263.7a9.13,9.13,0,0,1,9.42,9V48.2C273.12,52.07,270.29,57.52,264,57.52Zm-15.28-2.84H264a6.35,6.35,0,0,0,6.28-6.48V24.8c0-4-3.39-6.18-6.58-6.18h-15Z" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel9">
          <polygon points="286.81 54.68 286.68 38.12 304.2 38.12 304.2 35.28 286.66 35.28 286.53 18.62 307.1 18.62 307.1 15.78 283.67 15.78 283.99 57.52 307.6 57.52 307.6 54.68 286.81 54.68" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
        <clipPath id="clip-rel10">
          <rect x="316.8" y="54.63" width="23.9" height="2.83" transform="translate(-0.23 1.36) rotate(-0.24)" fill="#3614c4" stroke="#222222" stroke-width="200"/>
        </clipPath>
      </defs>
      <g id="whole-logo" opacity="0">
        <g id="rings">
          <circle cx="7.2" cy="36.61" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="39.94" cy="16.88" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="77.97" cy="54.68" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="108.95" cy="30.81" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="141.41" cy="46.88" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="175" cy="76.7" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="192.78" cy="16.88" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="233.6" cy="32.32" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="257.51" cy="56" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="287.1" cy="36.7" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
          <circle cx="328.75" cy="56" r="5.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1"/>
        </g>
        <g id="lines">
          <g>
            <path d="M12.17,33.62,35,19.87" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="27" stroke-dashoffset="27"></path>
            <path d="M12.82,38,72.17,53.2" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="62" stroke-dashoffset="62"></path>
          </g>
          <g>
            <path d="M82.56,51.14l21.8-16.79" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="28" stroke-dashoffset="28"></path>
            <path d="M83.72,54l51.94-6.38" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="53" stroke-dashoffset="53"></path>
            <path d="M44.05,21,73.86,50.59" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="43" stroke-dashoffset="43"></path>
            <path d="M45.63,18l57.64,11.63" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="59" stroke-dashoffset="59"></path>
          </g>
          <g>
            <path d="M147.21,43.49,187.78,19.8" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="47" stroke-dashoffset="47"></path>
            <path d="M145.74,50.73c6.44,5.73,18.46,16.43,24.91,22.14" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="34" stroke-dashoffset="34"></path>
            <path d="M114.15,33.38,136.22,44.31" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="25" stroke-dashoffset="25"></path>
            <path d="M114.15,28.24,187,16.88" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="74" stroke-dashoffset="74"></path>
          </g>
          <g>
            <path d="M198.58,19.07l29.6,11.2" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="32" stroke-dashoffset="32"></path>
            <path d="M176.56,70.9c3.33-11.52,11.1-37.16,14.53-48.47" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="51" stroke-dashoffset="51"></path>
            <path d="M179.62,73.2,229,35.81" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="62" stroke-dashoffset="62"></path>
            <path d="M180.8,75.24l71.09-17.83" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="74" stroke-dashoffset="74"></path>
          </g>
          <g>
            <path d="M262.36,52.83l19.89-13" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="24" stroke-dashoffset="24"></path>
            <path d="M237.72,36.4l15.67,15.52" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="23" stroke-dashoffset="23"></path>
            <path d="M239.45,32.79l41.88,3.36" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="43" stroke-dashoffset="43"></path>
            <path d="M262.36,56h61.13" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="62" stroke-dashoffset="62"></path>
          </g>
          <g>
            <path d="M292.37,39.14l31.12,14.42" fill="none" stroke="#3614c4" stroke-miterlimit="10" stroke-width="1" stroke-dasharray="35" stroke-dashoffset="35"></path>
          </g>
        </g>
        <g id="points">
          <g clip-path="url(#clip-rel1)">
            <circle id="point1" cx="7.2" cy="36.61" r="23" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel2)">
            <circle id="point2" cx="39.94" cy="16.88" r="50" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel3)">
            <circle id="point3" cx="77.97" cy="54.68" r="50" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel4)">
            <circle id="point4" cx="108.95" cy="30.81" r="37" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel5)">
            <circle id="point5" cx="141.41" cy="46.88" r="20" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel5)">
            <circle id="point6" cx="175" cy="76.7" r="80" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel6)">
            <circle id="point7" cx="192.78" cy="16.88" r="43" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel7)">
            <circle id="point8" cx="233.6" cy="32.32" r="35" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel8)">
            <circle id="point9" cx="257.51" cy="56" r="45" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel9)">
            <circle id="point10" cx="287.1" cy="36.7" r="35" fill="#3614c4"/>
          </g>
          <g clip-path="url(#clip-rel10)">
            <circle id="point11" cx="328.75" cy="56" r="20" fill="#3614c4"/>
          </g>
        </g>
      </g>
      <style>
        #relatude-anim #rings > circle:nth-child(1),
        #relatude-anim #point1 {
          transform-origin: 7.2px 36.61px;
        }
        #relatude-anim #rings > circle:nth-child(2),
        #relatude-anim #point2 {
          transform-origin: 39.94px 16.88px;
        }
        #relatude-anim #rings > circle:nth-child(3),
        #relatude-anim #point3 {
          transform-origin: 77.97px 54.68px;
        }
        #relatude-anim #rings > circle:nth-child(4),
        #relatude-anim #point4 {
          transform-origin: 108.95px 30.81px;
        }
        #relatude-anim #rings > circle:nth-child(5),
        #relatude-anim #point5 {
          transform-origin: 141.41px 46.88px;
        }
        #relatude-anim #rings > circle:nth-child(6),
        #relatude-anim #point6 {
          transform-origin: 175px 76.7px;
        }
        #relatude-anim #rings > circle:nth-child(7),
        #relatude-anim #point7 {
          transform-origin: 192.78px 16.88px;
        }
        #relatude-anim #rings > circle:nth-child(8),
        #relatude-anim #point8 {
          transform-origin: 233.6px 32.32px;
        }
        #relatude-anim #rings > circle:nth-child(9),
        #relatude-anim #point9 {
          transform-origin: 257.51px 56px;
        }
        #relatude-anim #rings > circle:nth-child(10),
        #relatude-anim #point10 {
          transform-origin: 287.1px 36.7px;
        }
        #relatude-anim #rings > circle:nth-child(11),
        #relatude-anim #point11 {
          transform-origin: 328.75px 56px;
        }

        #relatude-anim #rings > circle {
          opacity: 0;
          transform: scale(0.01);
        }
        #relatude-anim #rings > circle:nth-child(1) {
          animation: scaleIn ${0.4 * speed}s ease-out 0s forwards, scaleOut ${0.4 * speed}s ease-in 2.4s forwards;
        }
        #relatude-anim #rings > circle:nth-child(2),
        #relatude-anim #rings > circle:nth-child(3) {
          animation: scaleIn ${0.4 * speed}s ease-out ${0.3 * speed}s forwards, scaleOut ${0.4 * speed}s ease-in ${2.6 * speed}s forwards;
        }
        #relatude-anim #rings > circle:nth-child(4),
        #relatude-anim #rings > circle:nth-child(5) {
          animation: scaleIn ${0.4 * speed}s ease-out ${0.6 * speed}s forwards, scaleOut ${0.4 * speed}s ease-in ${2.8 * speed}s forwards;
        }
        #relatude-anim #rings > circle:nth-child(6),
        #relatude-anim #rings > circle:nth-child(7) {
          animation: scaleIn ${0.4 * speed}s ease-out ${0.9 * speed}s forwards, scaleOut ${0.4 * speed}s ease-in ${3 * speed}s forwards;
        }
        #relatude-anim #rings > circle:nth-child(8),
        #relatude-anim #rings > circle:nth-child(9) {
          animation: scaleIn ${0.4 * speed}s ease-out ${1.2 * speed}s forwards, scaleOut ${0.4 * speed}s ease-in ${3.2 * speed}s forwards;
        }
        #relatude-anim #rings > circle:nth-child(10),
        #relatude-anim #rings > circle:nth-child(11) {
          animation: scaleIn ${0.4 * speed}s ease-out ${1.5 * speed}s forwards, scaleOut ${0.4 * speed}s ease-in ${3.4 * speed}s forwards;
        }

        #relatude-anim #lines > g:nth-child(1) > path { animation: drawIn ${0.4 * speed}s ease-in ${0.3 * speed}s forwards; }
        #relatude-anim #lines > g:nth-child(2) > path { animation: drawIn ${0.4 * speed}s ease-in ${0.6 * speed}s forwards; }
        #relatude-anim #lines > g:nth-child(3) > path { animation: drawIn ${0.4 * speed}s ease-in-out ${0.6 * speed}s forwards; }
        #relatude-anim #lines > g:nth-child(4) > path { animation: drawIn ${0.4 * speed}s ease-in-out ${1.2 * speed}s forwards; }
        #relatude-anim #lines > g:nth-child(5) > path { animation: drawIn ${0.4 * speed}s ease-out ${1.5 * speed}s forwards; }
        #relatude-anim #lines > g:nth-child(6) > path { animation: drawIn ${0.4 * speed}s ease-out ${1.8 * speed}s forwards; }

        #relatude-anim #lines { animation: fadeOut ${0.5 * speed}s ease-out ${2.4 * speed}s forwards; }

        #relatude-anim #points circle {
          opacity: 0;
          transform: scale(0.01);
        }
        #relatude-anim #point1 { animation: scaleIn ${1 * speed}s ease-in-out ${2.6 * speed}s forwards; }
        #relatude-anim #point2 { animation: scaleIn ${1 * speed}s ease-in-out ${2.8 * speed}s  forwards; }
        #relatude-anim #point3 { animation: scaleIn ${1 * speed}s ease-in-out ${2.8 * speed}s  forwards; }
        #relatude-anim #point4 { animation: scaleIn ${1 * speed}s ease-in-out ${3 * speed}s  forwards; }
        #relatude-anim #point5 { animation: scaleIn  ${1 * speed}s ease-in-out ${3 * speed}s  forwards; }
        #relatude-anim #point6 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.2 * speed}s  forwards; }
        #relatude-anim #point7 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.2 * speed}s  forwards; }
        #relatude-anim #point8 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.4 * speed}s  forwards; }
        #relatude-anim #point9 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.4 * speed}s  forwards; }
        #relatude-anim #point10 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.6 * speed}s  forwards; }
        #relatude-anim #point11 { animation: scaleIn  ${1 * speed}s ease-in-out ${3.6 * speed}s  forwards; }

        #relatude-anim #points > g:nth-child(11) { animation: blink 1s linear ${5 * speed}s 2; }

        #relatude-anim #whole-logo {
          opacity: 0;
          transform-origin: 170px 43px;
          transform: scale(0.85);
          animation: scaleInLogo ${5 * speed}s ease-out 0s forwards, fadeIn ${5 * speed}s ease-in-out 0s forwards;
        }

        @keyframes scaleIn {
          1% {
            opacity: 1;
            transform: scale(0.01);
          }
          100% {
            opacity: 1;
            transform: scale(1);
          }
        }

        @keyframes scaleInLogo {
          100% {
            transform: scale(0.92);
          }
        }

        @keyframes scaleOut {
          99% {
            opacity: 1;
            transform: scale(1);
          }
          99% {
            opacity: 1;
            transform: scale(0.01);
          }
          100% {
            opacity: 0;
            transform: scale(0.01);
          }
        }

        @keyframes drawIn {
          to {
            stroke-dashoffset: 0px;
          }
        }

        @keyframes fadeOut {
          to {
            opacity: 0;
          }
        }

        @keyframes fadeIn {
          to {
            opacity: 1;
          }
        }

        @keyframes blink {
          0%, 49% {
            opacity: 0;
          }
          50%, 100% {
            opacity: 1
          }
        }

      </style>
    </svg>
    `).split("#3614c4").join(color).split("#222222").join(color);
