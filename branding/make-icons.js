// Makes the app's icon and logo images from the SVGs in this folder:
//   client/Nebo.Client/Assets/nebo.ico          window/exe icon (16-256 px, PNG entries)
//   client/Nebo.Client/Assets/nebo-dark.png     logo for the dark theme (About box)
//   client/Nebo.Client/Assets/nebo-light.png    logo for the light theme
//   streamdeck/icons/plugin*.png, category*.png  Stream Deck plugin and category icons
//                                                (copied into the plugin by build_assets.py)
//   server/setup/wizard*.png, wizard-small*.png  setup wizard side image and corner icon
// Run once after changing a logo:  cd branding && npm ci && node make-icons.js
// The outputs are committed, so building NEBO does not need Node.
const { Resvg } = require('@resvg/resvg-js');
const fs = require('fs');
const path = require('path');

const here = __dirname;
const repo = path.join(here, '..');
const assets = path.join(repo, 'client', 'Nebo.Client', 'Assets');
const deck = path.join(repo, 'streamdeck', 'icons');

function png(svgFile, width) {
    return render(fs.readFileSync(path.join(here, svgFile), 'utf8'), width);
}
function render(svg, width) {
    return new Resvg(svg, { fitTo: { mode: 'width', value: width }, font: { loadSystemFonts: true } }).render().asPng();
}

// ICO with PNG-compressed entries (Windows Vista and later read these at every size).
function ico(images) {
    const header = Buffer.alloc(6 + 16 * images.length);
    header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
    let offset = header.length;
    images.forEach(({ size, data }, i) => {
        const e = 6 + 16 * i;
        header.writeUInt8(size >= 256 ? 0 : size, e);
        header.writeUInt8(size >= 256 ? 0 : size, e + 1);
        header.writeUInt16LE(1, e + 4);       // planes
        header.writeUInt16LE(32, e + 6);      // bits per pixel
        header.writeUInt32LE(data.length, e + 8);
        header.writeUInt32LE(offset, e + 12);
        offset += data.length;
    });
    return Buffer.concat([header, ...images.map(x => x.data)]);
}

fs.mkdirSync(assets, { recursive: true });
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
fs.writeFileSync(path.join(assets, 'nebo.ico'), ico(sizes.map(size => ({ size, data: png('nebo-icon.svg', size) }))));
fs.writeFileSync(path.join(assets, 'nebo-dark.png'), png('nebo-dark.svg', 900));
fs.writeFileSync(path.join(assets, 'nebo-light.png'), png('nebo-light.svg', 900));
fs.mkdirSync(deck, { recursive: true });
for (const [name, size] of [['plugin', 128], ['category', 28]]) {
    fs.writeFileSync(path.join(deck, `${name}.png`), png('nebo-icon.svg', size));
    fs.writeFileSync(path.join(deck, `${name}@2x.png`), png('nebo-icon.svg', size * 2));
}
// Setup wizard (Inno Setup): the icon on the logo's dark background beside the welcome page, and
// the icon in the corner of the other pages; 100 % and 200 % sizes.
const setup = path.join(repo, 'server', 'setup');
fs.mkdirSync(setup, { recursive: true });
const icon = fs.readFileSync(path.join(here, 'nebo-icon.svg'), 'utf8').replace('width="820" height="820"', 'x="22" y="80" width="120" height="120"');
const side = `<svg xmlns="http://www.w3.org/2000/svg" width="164" height="314" viewBox="0 0 164 314"><rect width="164" height="314" fill="#10161E"/>${icon}</svg>`;
for (const [suffix, k] of [['', 1], ['@2x', 2]]) {
    fs.writeFileSync(path.join(setup, `wizard${suffix}.png`), render(side, 164 * k));
    fs.writeFileSync(path.join(setup, `wizard-small${suffix}.png`), png('nebo-icon.svg', 55 * k));
}
console.log('icons written');
