// Makes the setup wizard's option pictures (drawn mockups of the controller's scope, in the
// map style's colours) for the server settings pages:
//   server/setup/options/<setting>-<choice>.png   200x130 DIP, rendered at 2x
// Run after changing a picture:  cd branding && npm ci && node make-options.js
// The outputs are committed, so building NEBO does not need Node.
const { Resvg } = require('@resvg/resvg-js');
const fs = require('fs');
const path = require('path');

const style = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'style', 'map-style.json'), 'utf8'));
const out = path.join(__dirname, '..', 'server', 'setup', 'options');
const W = 200, H = 130;
const FR = style.identity.friendly, HO = style.identity.hostile, UN = style.identity.unknown;
const BG = '#10161E', GRID = '#22303F', TEXT = '#C8D2DC', DIM = '#6E7C8A', OK = '#9EE37D';
const MONO = 'Consolas', UI = 'Segoe UI';

function scope(body) {
    let grid = '';
    for (let x = 25; x < W; x += 50) grid += `<line x1="${x}" y1="0" x2="${x}" y2="${H}"/>`;
    for (let y = 15; y < H; y += 50) grid += `<line x1="0" y1="${y}" x2="${W}" y2="${y}"/>`;
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">
<rect width="${W}" height="${H}" fill="${BG}"/><g stroke="${GRID}" stroke-width="0.6">${grid}</g>${body}</svg>`;
}
const label = (x, y, t, c = TEXT, size = 7) =>
    `<text x="${x}" y="${y}" font-family="${MONO}" font-size="${size}" fill="${c}" stroke="#000" stroke-width="2" paint-order="stroke">${t}</text>`;
const caption = (t, c = DIM) => `<text x="${W / 2}" y="${H - 5}" text-anchor="middle" font-family="${UI}" font-size="7.5" fill="${c}">${t}</text>`;
// Air tracks (open-bottom frames as on the scope) with a 60 s speed vector.
const airFriendly = (x, y, dx = 14, dy = 0) => `<g fill="none" stroke="${FR}" stroke-width="1.4"><path d="M${x - 5},${y + 2} A5,5 0 0 1 ${x + 5},${y + 2}"/><line x1="${x}" y1="${y}" x2="${x + dx}" y2="${y + dy}" stroke-width="0.8"/></g>`;
const airHostile = (x, y, dx = -14, dy = 0) => `<g fill="none" stroke="${HO}" stroke-width="1.4"><path d="M${x - 5},${y + 2} L${x},${y - 4} L${x + 5},${y + 2}"/><line x1="${x}" y1="${y}" x2="${x + dx}" y2="${y + dy}" stroke-width="0.8"/></g>`;
const airUnknown = (x, y, dx = -14, dy = 0) => `<g fill="none" stroke="${UN}" stroke-width="1.4"><path d="M${x - 5},${y + 2} L${x - 5},${y - 4} L${x + 5},${y - 4} L${x + 5},${y + 2}"/><line x1="${x}" y1="${y}" x2="${x + dx}" y2="${y + dy}" stroke-width="0.8"/></g>`;
const groundHostile = (x, y) => `<path d="M${x},${y - 5} L${x + 5},${y} L${x},${y + 5} L${x - 5},${y} Z" fill="none" stroke="${HO}" stroke-width="1.4"/>`;
const ring = (x, y, r) => `<circle cx="${x}" cy="${y}" r="${r}" fill="none" stroke="${HO}" stroke-width="0.8" stroke-dasharray="4.5 3.6"/>`;
const awacs = (x, y) => airFriendly(x, y, 0, 0) + label(x + 7, y + 3, 'E-3', FR);

// The same picture with and without the enemy ground units.
const air = awacs(30, 100) + airHostile(150, 30) + label(158, 33, 'MIG29 250', HO) + airFriendly(40, 40) + label(48, 37, 'VIPER1', FR);
const pics = {
    'ground-shown': scope(air + ring(120, 75, 26) + groundHostile(120, 75) + label(127, 78, 'SA-10', HO)
        + groundHostile(146, 92) + label(153, 95, 'SA-15', HO) + groundHostile(100, 62) + caption('Detected SAMs and vehicles on the scope')),
    'ground-hidden': scope(air + `<g stroke="${UN}" stroke-width="1" stroke-dasharray="6 3" opacity="0.8"><line x1="40" y1="40" x2="140" y2="88"/><line x1="40" y1="110" x2="150" y2="62"/></g>`
        + label(122, 58, 'RWR fix?', UN) + caption('Found by RWR triangulation with pilots')),

    'ident-auto': scope(awacs(30, 100) + airHostile(120, 40) + label(128, 43, 'MIG29 250', HO) + airHostile(150, 75) + label(158, 78, 'SU27 310', HO)
        + airFriendly(45, 45) + label(53, 42, 'VIPER1', FR) + caption('The type shows once DCS identifies it')),
    'ident-manual': scope(awacs(30, 100) + airUnknown(120, 40) + label(128, 43, 'UNK 250', UN) + airUnknown(150, 75) + label(158, 78, 'UNK 310', UN)
        + airFriendly(45, 45) + label(53, 42, 'VIPER1', FR)
        + `<rect x="128" y="84" width="62" height="24" rx="2" fill="#1C2632" stroke="#3A4A5A" stroke-width="0.6"/>`
        + label(132, 94, 'NCTR', TEXT, 6.5) + label(132, 103, 'Identify as…', TEXT, 6.5) + caption('Controllers identify contacts themselves')),
};

// Radar scan: one hostile track, dots where the scope drew it over the last 30 s.
function trail(step) {
    let dots = '';
    for (let t = 30; t > 0; t -= step) dots += `<circle cx="${150 - t * 2.6}" cy="${60 + t * 0.5}" r="1.3" fill="${HO}" opacity="${0.25 + 0.6 * (1 - t / 30)}"/>`;
    return dots;
}
pics['scan-on'] = scope(trail(10) + airHostile(150, 60, 14, -3) + label(158, 54, 'MIG29', HO) + awacs(30, 105)
    + label(100, 92, 'E-3: every 10 s', DIM, 6.5) + caption('Each radar refreshes at its own scan rate'));
pics['scan-off'] = scope(trail(1) + airHostile(150, 60, 14, -3) + label(158, 54, 'MIG29', HO) + awacs(30, 105)
    + label(100, 92, 'every second', DIM, 6.5) + caption('Every contact every second'));

// Flying controllers: one home address, a pilot in a cockpit and a controller login.
function home(allowed) {
    const mark = allowed
        ? `<path d="M137,58 l5,5 l9,-11" fill="none" stroke="${OK}" stroke-width="2.4"/>`
        : `<g stroke="${HO}" stroke-width="2.4"><line x1="136" y1="51" x2="150" y2="65"/><line x1="150" y1="51" x2="136" y2="65"/></g>`;
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}"><rect width="${W}" height="${H}" fill="${BG}"/>
<g fill="none" stroke="${TEXT}" stroke-width="1.2">
<path d="M18,62 L48,38 L78,62 L78,98 L18,98 Z"/>
<path d="M30,84 l14,-6 l14,6 l-14,-3 Z" fill="${FR}" stroke="${FR}"/>
<rect x="56" y="72" width="16" height="11" rx="1"/><line x1="64" y1="83" x2="64" y2="88"/>
<path d="M78,70 C100,60 112,58 124,58" stroke="${DIM}" stroke-dasharray="3 2"/>
<rect x="124" y="44" width="40" height="28" rx="3" stroke="${allowed ? OK : HO}"/></g>
${mark}${label(20, 112, 'one address (router)', DIM, 6.5)}${label(122, 84, 'NEBO login', TEXT, 6.5)}
${caption(allowed ? 'Controller login allowed while a pilot flies' : 'Refused while someone there sits in a cockpit', allowed ? DIM : DIM)}</svg>`;
}
pics['flying-no'] = home(false);
pics['flying-yes'] = home(true);

// DTC: the client's DTC tab pushing a cartridge to a pilot, or switched off.
function dtc(on) {
    const c = on ? TEXT : '#46525E';
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}"><rect width="${W}" height="${H}" fill="${BG}"/>
<rect x="10" y="14" width="96" height="92" rx="3" fill="#18212B" stroke="#3A4A5A" stroke-width="0.8"/>
<text x="16" y="25" font-family="${UI}" font-size="7.5" fill="${c}">DTC</text>
${[0, 1, 2].map(i => `<text x="16" y="${40 + i * 11}" font-family="${MONO}" font-size="6.5" fill="${c}">WP${i + 1}  ${['N42°11 E042°02', 'N42°31 E041°48', 'N42°45 E041°20'][i]}</text>`).join('')}
<rect x="16" y="82" width="56" height="15" rx="2" fill="${on ? '#2B5C8F' : '#252E38'}" stroke="${on ? FR : '#3A4A5A'}" stroke-width="0.8"/>
<text x="44" y="92" text-anchor="middle" font-family="${UI}" font-size="7" fill="${on ? '#FFFFFF' : '#56626E'}">Push to VIPER1</text>
${on ? `<path d="M110,60 C130,48 146,48 160,56" fill="none" stroke="${FR}" stroke-width="1.2" stroke-dasharray="3 2"/><path d="M160,56 l-6,-1 l3,5 Z" fill="${FR}"/>` : ''}
<path d="M164,66 l14,-6 l14,6 l-14,-3 Z" fill="${on ? FR : '#3A4A5A'}" stroke="${on ? FR : '#3A4A5A'}"/>
${label(158, 80, on ? 'cartridge' : '', TEXT, 6.5)}${label(158, 88, on ? 'loaded' : '', TEXT, 6.5)}
${caption(on ? 'Controllers push cartridges to pilots' : 'Pilot sync off: no DTC pushes')}</svg>`;
}
pics['dtc-on'] = dtc(true);
pics['dtc-off'] = dtc(false);

fs.mkdirSync(out, { recursive: true });
for (const [name, svg] of Object.entries(pics)) {
    const png = new Resvg(svg, { fitTo: { mode: 'width', value: W * 2 }, font: { loadSystemFonts: true } }).render().asPng();
    fs.writeFileSync(path.join(out, name + '.png'), png);
    console.log(name + '.png', png.length);
}
