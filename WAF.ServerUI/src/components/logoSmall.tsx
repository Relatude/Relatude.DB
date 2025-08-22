import React from "react";
import { useApp } from "../start/useApp";

const component = (p: { padding?: string, color?: string }) => {
    const app = useApp();
    const color = app.ui.darkTheme ? "#CCC" : "#000";
    return (
        <svg viewBox="0 0 396.64 101.52" height="100%" style={{ fill: color, padding: p.padding }}>
            <g id="Layer_2" data-name="Layer 2"><g id="Layer_1-2" data-name="Layer 1">
                <path d="M76.61,35.33V30.18A11.71,11.71,0,0,0,65,18.14h-21v51H50V47H62.69L74.07,69.13h6.8L69.18,46.24a11.34,11.34,0,0,0,7.43-10.91M50,24.05H64.42c3.84,0,6.13,2.24,6.13,6v5.08c0,3.75-2.29,6-6.13,6H50Z" />
                <polygon points="93.34 46.59 113.73 46.59 113.73 40.75 93.34 40.75 93.34 24.05 117.09 24.05 117.09 18.14 87.28 18.14 87.28 69.13 117.66 69.13 117.66 63.14 93.34 63.14 93.34 46.59" />
                <polygon points="130.46 18.14 124.41 18.14 124.41 69.13 153.64 69.13 153.64 63.14 130.46 63.14 130.46 18.14" />
                <polygon points="198.58 24.05 213.17 24.05 213.17 69.13 219.23 69.13 219.23 24.05 233.82 24.05 233.82 18.14 198.58 18.14 198.58 24.05" />
                <path d="M268.4,52.65c0,9.94-7.39,11.42-11.79,11.42s-11.78-1.48-11.78-11.42V18.14h-6.05v35.3c0,10.31,6.66,16.47,17.83,16.47s17.84-6.16,17.84-16.47V18.14H268.4Z" />
                <path d="M307,18.14H284.41v51H307a11.71,11.71,0,0,0,11.61-12V30.18a11.71,11.71,0,0,0-11.61-12m-16.5,45V24.05H306a6.18,6.18,0,0,1,6.56,6.35V56.87A6.11,6.11,0,0,1,306,63.14Z" />
                <polygon points="334.02 46.59 354.41 46.59 354.41 40.75 334.02 40.75 334.02 24.05 357.78 24.05 357.78 18.14 327.97 18.14 327.97 69.13 358.35 69.13 358.35 63.14 334.02 63.14 334.02 46.59" />
                <path d="M203.65,78.57l-.6,0L175.87,0h-6.49l16.8,48.12H170.54l5.38-15.59-5.77-1.78L156.92,69.12h6.36l5.15-15H188.3l9,26.37a11.45,11.45,0,1,0,6.3-1.9m6,11.48a6,6,0,1,1-6-6,6,6,0,0,1,6,6" />
                <rect x="366.39" y="63.1" width="30.25" height="6.02" id="cursor" />
                <path d="M18.87,41.07a9.62,9.62,0,1,0-.15,5.9H36.05v-5.9ZM9.64,40A3.74,3.74,0,1,1,5.9,43.78,3.74,3.74,0,0,1,9.64,40" />
                <animate attributeType="CSS" attributeName="opacity" from="1" to="0" dur="200ms" xlinkHref="#cursor" id="a1" begin="0;a4.end" />
                <animate attributeType="CSS" attributeName="opacity" from="0" to="0" dur="200ms" xlinkHref="#cursor" id="a2" begin="a1.end" />
                <animate attributeType="CSS" attributeName="opacity" from="0" to="1" dur="200ms" xlinkHref="#cursor" id="a3" begin="a2.end" />
                <animate attributeType="CSS" attributeName="opacity" from="1" to="1" dur="600ms" xlinkHref="#cursor" id="a4" begin="a3.end" />
            </g></g>
        </svg>
    )
}

export default component;