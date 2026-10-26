// Renders every page of a .drawio file to PNG with mxGraph (draw.io's engine) in headless Chrome.
//   node render-drawio.js <file.drawio> <outDir> [chromePath]
// Chrome/Chromium path: 3rd argument, or the CHROME_PATH environment variable.
const fs = require('fs');
const path = require('path');
const puppeteer = require('puppeteer-core');

const [, , drawioPath, outDir, chromeArg] = process.argv;
const chrome = chromeArg || process.env.CHROME_PATH || (process.platform === 'win32'
  ? 'C:/Program Files/Google/Chrome/Application/chrome.exe'
  : '/usr/bin/google-chrome');
const mxDir = path.resolve(__dirname, 'node_modules/mxgraph/javascript').replace(/\\/g, '/');

// Core mxGraph lacks a few draw.io extension shapes: map them to close core equivalents (rendering only).
const xml = fs.readFileSync(drawioPath, 'utf8')
  .replace(/shape=cylinder3/g, 'shape=cylinder')
  .replace(/shape=umlActor/g, 'shape=actor');

const htmlPath = path.join(__dirname, '.render-page.html');
fs.writeFileSync(htmlPath, `<!doctype html><html><head><meta charset="utf-8">
<script>var mxBasePath='file:///${mxDir.replace(/^\//, '')}/src'; var mxLoadResources=false; var mxLoadStylesheets=false;</script>
<script src="file:///${mxDir.replace(/^\//, '')}/mxClient.js"></script>
<style>body{margin:0;background:#fff;font-family:Helvetica,Arial,sans-serif}</style></head>
<body><div id="g" style="position:absolute;left:0;top:0;width:4000px;height:4000px;overflow:hidden"></div></body></html>`);

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await puppeteer.launch({ executablePath: chrome, headless: true, args: ['--no-sandbox', '--allow-file-access-from-files', '--disable-gpu'] });
  const page = await browser.newPage();
  await page.setViewport({ width: 4000, height: 4000, deviceScaleFactor: 2 });
  await page.goto('file:///' + htmlPath.replace(/\\/g, '/').replace(/^\//, ''));

  const count = await page.evaluate((x) => mxUtils.parseXml(x).getElementsByTagName('diagram').length, xml);
  for (let i = 0; i < count; i++) {
    const info = await page.evaluate((x, index) => {
      const diagram = mxUtils.parseXml(x).getElementsByTagName('diagram')[index];
      const container = document.getElementById('g');
      container.innerHTML = '';
      const graph = new mxGraph(container);
      graph.setHtmlLabels(true);
      graph.setEnabled(false);
      graph.foldingEnabled = false;
      const ss = graph.getStylesheet();
      Object.assign(ss.getDefaultVertexStyle(), { fillColor: '#FFFFFF', strokeColor: '#555555', fontColor: '#1A1A1A', fontFamily: 'Helvetica', fontSize: 11 });
      Object.assign(ss.getDefaultEdgeStyle(), { strokeColor: '#555555', fontColor: '#333333', fontFamily: 'Helvetica', fontSize: 10 });
      ss.putCellStyle('text', { strokeColor: 'none', fillColor: 'none', shape: 'label' });
      ss.putCellStyle('swimlane', Object.assign({}, ss.getDefaultVertexStyle(), { shape: 'swimlane', fontStyle: 1, startSize: 30, verticalAlign: 'top' }));
      // Decode each page in isolation: ids such as "0" and "1" repeat across pages.
      const single = mxUtils.parseXml(mxUtils.getXml(diagram.getElementsByTagName('mxGraphModel')[0]));
      new mxCodec(single).decode(single.documentElement, graph.getModel());
      const b = graph.getGraphBounds();
      return { name: diagram.getAttribute('name'), x: b.x, y: b.y, w: b.width, h: b.height };
    }, xml, i);

    await new Promise((r) => setTimeout(r, 300));
    const pad = 20;
    const file = path.join(outDir, `${String(i + 1).padStart(2, '0')}-${info.name.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}.png`);
    await page.screenshot({ path: file, clip: { x: Math.max(0, info.x - pad), y: Math.max(0, info.y - pad), width: Math.ceil(info.w + 2 * pad), height: Math.ceil(info.h + 2 * pad) } });
    console.log(`${file}  (${Math.round(info.w)}x${Math.round(info.h)})`);
  }
  await browser.close();
  fs.unlinkSync(htmlPath);
})().catch((e) => { console.error(e); process.exit(1); });
