// Makes the GitHub page art around the NEBO logo (nebo-dark.svg) and the Kestrel's Nest emblem:
// a dawn sky in the flag's colours, the radar O's range rings and sweep over it, radar tracks.
//   docs/images/banner.png         README banner, 1280x400 (rendered at 2x)
//   docs/images/divider.png        thin section divider, 1280x24 (2x)
//   branding/social-preview.png    repo Settings -> Social preview, 1280x640
// Run after changing the logo:  cd branding && npm ci && node make-banner.js
const { Resvg } = require('@resvg/resvg-js');
const fs = require('fs');
const path = require('path');

const here = __dirname;
const images = path.join(here, '..', 'docs', 'images');
const FR = '#4DA3FF', HO = '#FF5A5A', YEL = '#FFD500', BLU = '#005BBB';
const MONO = 'JetBrains Mono, Consolas', UI = 'Segoe UI';

// The logo as a nested SVG, background removed, cropped to its drawn part.
const CROP = { x: 430, y: 380, w: 2300, h: 1060 };
const O = { x: 2416 - CROP.x, y: 743.2 - CROP.y };  // the radar centre in crop units
function logo(x, y, h) {
    let s = fs.readFileSync(path.join(here, 'nebo-dark.svg'), 'utf8')
        .replace(/<rect id="background"[^>]*\/>/, '').replace(/<\?xml[^>]*>/, '');
    const w = h * CROP.w / CROP.h, k = h / CROP.h;
    s = s.replace(/<svg[^>]*>/, `<svg xmlns="http://www.w3.org/2000/svg" x="${x}" y="${y}" width="${w}" height="${h}" viewBox="${CROP.x} ${CROP.y} ${CROP.w} ${CROP.h}">`);
    return { svg: s, cx: x + O.x * k, cy: y + O.y * k };
}
const emblemB64 = fs.readFileSync(path.join(here, 'kestrels-nest.png')).toString('base64');
const emblem = (x, y, h, op) => `<image x="${x}" y="${y}" height="${h}" width="${h * 300 / 734}" opacity="${op}" href="data:image/png;base64,${emblemB64}"/>`;

let seed = 1;
const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
const text = (x, y, t, c, size, extra = '') => `<text x="${x}" y="${y}" font-family="${MONO}" font-size="${size}" fill="${c}" ${extra}>${t}</text>`;
// Scope symbols: friendly arc, hostile chevron, each with its speed vector along a.
function track(x, y, a, hostile, s) {
    const c = hostile ? HO : FR;
    const frame = hostile ? `M${x - 6 * s},${y + 2 * s} L${x},${y - 5 * s} L${x + 6 * s},${y + 2 * s}` : `M${x - 6 * s},${y + 2 * s} A${6 * s},${6 * s} 0 0 1 ${x + 6 * s},${y + 2 * s}`;
    return `<g fill="none" stroke="${c}" stroke-width="${1.6 * s}"><path d="${frame}"/><line x1="${x}" y1="${y}" x2="${x + Math.cos(a) * 26 * s}" y2="${y + Math.sin(a) * 26 * s}" stroke-width="${s}"/></g>`;
}

function art(W, H, social) {
    const L = logo(W / 2 - (social ? 330 : 255), social ? 110 : 60, social ? 300 : 232);
    let g = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">
<defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#060B16"/><stop offset="0.55" stop-color="#0D2A57"/>
<stop offset="0.82" stop-color="${BLU}"/><stop offset="0.95" stop-color="#E9B800"/><stop offset="1" stop-color="${YEL}"/></linearGradient></defs>
<rect width="${W}" height="${H}" fill="url(#sky)"/>`;
    seed = 3;
    for (let i = 0; i < 90; i++) g += `<circle cx="${rnd() * W}" cy="${rnd() * H * 0.45}" r="${rnd() * 1.1 + 0.2}" fill="#fff" opacity="${0.2 + rnd() * 0.6}"/>`;
    // range rings and bearing lines centred on the logo's O, its sweep carried across the sky
    for (let r = 70; r < 1400; r += 70) g += `<circle cx="${L.cx}" cy="${L.cy}" r="${r}" fill="none" stroke="#BFD4F0" stroke-opacity="${r % 280 ? 0.06 : 0.13}"/>`;
    for (let d = 0; d < 360; d += 30) {
        const a = d * Math.PI / 180;
        g += `<line x1="${L.cx + Math.cos(a) * 140}" y1="${L.cy + Math.sin(a) * 140}" x2="${L.cx + Math.cos(a) * 1400}" y2="${L.cy + Math.sin(a) * 1400}" stroke="#BFD4F0" stroke-opacity="0.05"/>`;
    }
    for (let i = 0; i < 40; i++) {
        const a0 = (146 + i * 1.6) * Math.PI / 180, a1 = (146 + (i + 1) * 1.6) * Math.PI / 180, R = 1500;
        g += `<path d="M${L.cx},${L.cy} L${L.cx + Math.cos(a0) * R},${L.cy + Math.sin(a0) * R} L${L.cx + Math.cos(a1) * R},${L.cy + Math.sin(a1) * R}Z" fill="${YEL}" fill-opacity="${0.09 * (1 - i / 40)}"/>`;
    }
    // radar tracks: fading history dots, the symbol, its label
    const tracks = [[-40, H * 0.86, W * 0.22, H * 0.56, 'VIPER 1-1', 'F16C 250', false],
        [W + 40, H * 0.92, W * 0.79, H * 0.5, 'MIG29 280', '', true],
        social ? [W * 0.02, H + 10, W * 0.11, H * 0.68, 'HORNET 3', 'FA18C 310', false] : [W * 0.1, H + 10, W * 0.2, H * 0.8, 'HORNET 3', 'FA18C 310', false]];
    for (const [x1, y1, x2, y2, l1, l2, hostile] of tracks) {
        const c = hostile ? HO : FR, a = Math.atan2(y2 - y1, x2 - x1);
        for (let t = 0.35; t < 0.97; t += 0.07) g += `<circle cx="${x1 + (x2 - x1) * t}" cy="${y1 + (y2 - y1) * t}" r="1.6" fill="${c}" opacity="${0.15 + 0.6 * t}"/>`;
        // the label sits on the side away from the speed vector: upper left for eastbound, upper right for westbound
        const lx = Math.cos(a) > 0 ? x2 - 14 : x2 + 16, anchor = Math.cos(a) > 0 ? 'end' : 'start', fs = social ? 12 : 11;
        const halo = `text-anchor="${anchor}" stroke="#05080F" stroke-width="2.5" paint-order="stroke"`;
        g += track(x2, y2, a, hostile, 1.3) + text(lx, y2 - 14, l1, c, fs, halo);
        if (l2) g += text(lx, y2 - 14 + fs + 3, l2, c, fs, 'opacity="0.85" ' + halo);
    }
    let m = `M0,${H}`;
    seed = 9;
    for (let x = 0; x <= W; x += 40) m += ` L${x},${H - 18 - rnd() * 28 - (Math.sin(x / 170) + 1) * 12}`;
    g += `<path d="${m} L${W},${H}Z" fill="#05080F" opacity="0.92"/>` + L.svg + emblem(W - 58, H - 88, 70, 0.6);
    if (social) g += `<text x="${W / 2}" y="${H - 130}" text-anchor="middle" font-family="${UI}" font-size="25" fill="#EAF1F8">GCI and ATC for DCS World multiplayer · free and open source</text>
<text x="${W / 2}" y="${H - 98}" text-anchor="middle" font-family="${MONO}" font-size="16" fill="#C8D6E6">github.com/kestrelsnest/nebo</text>`;
    return g + '</svg>';
}

// A hairline from blue to the flag's yellow and back, with range ticks and a centre dot.
function divider(W, H) {
    let g = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">
<defs><linearGradient id="d" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${BLU}" stop-opacity="0"/><stop offset="0.3" stop-color="${BLU}"/>
<stop offset="0.5" stop-color="${YEL}"/><stop offset="0.7" stop-color="${BLU}"/><stop offset="1" stop-color="${BLU}" stop-opacity="0"/></linearGradient></defs>
<rect x="0" y="${H / 2 - 0.75}" width="${W}" height="1.5" fill="url(#d)"/>`;
    for (let x = W / 2 - 300; x <= W / 2 + 300; x += 30) if (x !== W / 2)
        g += `<rect x="${x - 0.5}" y="${H / 2 - (Math.abs(x - W / 2) % 90 ? 3 : 6)}" width="1" height="${Math.abs(x - W / 2) % 90 ? 6 : 12}" fill="#8D99AE" opacity="${0.6 - Math.abs(x - W / 2) / 600}"/>`;
    return g + `<circle cx="${W / 2}" cy="${H / 2}" r="4" fill="${YEL}"/><circle cx="${W / 2}" cy="${H / 2}" r="8" fill="none" stroke="${YEL}" stroke-opacity="0.5"/></svg>`;
}

function save(file, svg, width) {
    const png = new Resvg(svg, { fitTo: { mode: 'width', value: width }, font: { loadSystemFonts: true } }).render().asPng();
    fs.writeFileSync(file, png);
    console.log(path.relative(path.join(here, '..'), file), Math.round(png.length / 1024) + ' KB');
}
save(path.join(images, 'banner.png'), art(1280, 400, false), 2560);
save(path.join(images, 'divider.png'), divider(1280, 24), 2560);
save(path.join(here, 'social-preview.png'), art(1280, 640, true), 1280);
