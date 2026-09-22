// Real executable + named pipe + MCP + WPF integration; never touches an existing editor.
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const exe = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
fs.mkdirSync(output, { recursive: true });
const ready = path.join(output, `ready-${Date.now()}.json`);
const host = spawn(exe, ['--automation-headless', ready], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let hostErrors = ''; host.stderr.on('data', data => hostErrors += data);
let mcp, clientErrors = '', nextId = 0;
const pending = new Map();
const checks = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function rpc(method, params) {
  const id = ++nextId;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(Error(`Timeout: ${method}`)); }, 120000);
    pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject });
    mcp.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
async function main() {
  for (let i = 0; i < 150 && !fs.existsSync(ready); i++) { if (host.exitCode !== null) throw Error(hostErrors); await delay(100); }
  assert(fs.existsSync(ready), 'headless host ready');
  const sessionId = JSON.parse(fs.readFileSync(ready, 'utf8')).sessionId;
  const cli = spawnSync(exe, ['--automation-list'], { windowsHide: true, encoding: 'utf8', timeout: 15000 });
  assert.equal(cli.status, 0, cli.stderr);
  assert(JSON.parse(cli.stdout).some(s => s.sessionId === sessionId)); checks.push('CLI discovers explicit headless session');
  mcp = spawn(exe, ['--mcp'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  mcp.stderr.on('data', data => clientErrors += data);
  readline.createInterface({ input: mcp.stdout }).on('line', line => {
    try { const value = JSON.parse(line); const item = pending.get(value.id); if (item) { pending.delete(value.id); item.resolve(value); } }
    catch (error) { for (const item of pending.values()) item.reject(error); }
  });
  const init = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'morupixel-smoke', version: '1' } });
  assert.equal(init.result.protocolVersion, '2025-11-25');
  mcp.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const catalog = await rpc('tools/list', {}); assert.equal(catalog.result.tools.length, 29); checks.push('MCP initialization and 29 tools');
  async function call(command, args = {}, success = true) {
    const reply = await rpc('tools/call', { name: 'morupixel_' + command, arguments: { ...(command === 'list_sessions' ? {} : { sessionId }), ...args } });
    assert(!reply.error, JSON.stringify(reply.error));
    if (!success) { assert(reply.result.isError, 'command must fail'); return reply.result; }
    assert(!reply.result.isError, JSON.stringify(reply.result));
    return reply.result;
  }
  function data(result) { return result.structuredContent || JSON.parse(result.content.find(c => c.type === 'text').text); }
  const sessions = data(await call('list_sessions')); assert(JSON.stringify(sessions).includes(sessionId));
  const capabilities = data(await call('get_capabilities'));
  assert.equal(capabilities.contractVersion, 3); assert.equal(capabilities.commands.length, 29);
  assert(capabilities.unsupportedViaMcp.includes('3d_uv_mapping')); assert.equal(capabilities.materials.embeddedOriginals, true);
  checks.push('Live capabilities identify supported and future operations');
  let state = data(await call('get_state')); assert.equal(state.documents.length, 0);
  state = data(await call('new_document', { name: 'AI 연결 데모', width: 960, height: 600, background: '#141B29' }));
  let documentId = state.documentId;
  async function edit(command, args = {}) { state = data(await call(command, { documentId, expectedRevision: state.revision, ...args })); return state; }
  await edit('add_shape', { shape: 'rectangle', width: 860, height: 4, x: 50, y: 52, fill: '#FA9261', name: 'Accent' });
  await edit('add_text', { text: 'MORUPIXEL', fontFamily: 'Segoe UI', fontSize: 24, tracking: 150, x: 50, y: 78, color: '#FA9261' });
  await edit('add_text', { text: '말로 시작하는\n새로운 편집.', fontFamily: 'Malgun Gothic', fontSize: 56, bold: true, lineHeight: 80, x: 46, y: 158, color: '#FFFFFF', name: '제목' });
  const titleId = state.layerId;
  await edit('update_text', { layerId: titleId, text: '말로 시작하는\n새로운 편집', tracking: -25 });
  await edit('add_text', { text: 'MCP · 로컬 명령\n텍스트와 도형은 편집 가능한 레이어로.', fontFamily: 'Malgun Gothic', fontSize: 20, lineHeight: 34, x: 50, y: 376, color: '#C6CEDB' });
  const sample = path.resolve(__dirname, '../../assets/samples/sea-window.png');
  await edit('add_image', { path: sample, x: 524, y: 170, name: '사진' });
  const imageId = state.layerId;
  await edit('set_layer', { layerId: imageId, scaleX: .24, scaleY: .24 });
  await edit('reorder_layer', { layerId: imageId, direction: 'down' });
  await edit('add_adjustment', { kind: 'exposure', exposure: .1, name: '밝기' });
  const adjustmentId = state.layerId;
  const revision = state.revision;
  await edit('delete_layer', { layerId: adjustmentId });
  await edit('undo'); await edit('redo');
  await call('set_layer', { documentId, expectedRevision: revision, layerId: imageId, x: 20 }, false);
  checks.push('Text, shape, image, layer transform/order, adjustment, delete, undo/redo and stale revision');
  const compact = data(await call('get_state', { documentId, includeLayers: false })).documents[0];
  assert.equal(compact.layersIncluded, false); assert(!Object.hasOwn(compact, 'layers')); assert.equal(compact.artboardCount, 1);
  const query = data(await call('query_layers', { documentId, expectedRevision: compact.revision, category: 'Drawing', nameContains: '제목', limit: 1 }));
  assert.equal(query.totalMatches, 1); assert.equal(query.layers[0].layerId, titleId);
  const detail = data(await call('get_layer', { documentId, expectedRevision: compact.revision, layerId: titleId }));
  assert.equal(detail.layer.category, 'Drawing'); assert.equal(detail.layer.positionSpace, 'parent');
  const batch = { documentId, expectedRevision: compact.revision, operationId: randomUUID(), label: 'Layout refinement', steps: [
    { command: 'set_layer', arguments: { layerId: titleId, x: 48 } },
    { command: 'set_layer', arguments: { layerId: imageId, opacity: .95 } }
  ] };
  const validation = data(await call('apply_batch', { ...batch, dryRun: true }));
  assert.equal(validation.committed, false); assert.equal(validation.wouldChange, true);
  assert.equal(data(await call('get_state', { documentId, includeLayers: false })).documents[0].revision, compact.revision);
  state = data(await call('apply_batch', batch)); assert.equal(state.undoSteps, 1);
  const committedRevision = state.revision;
  const replay = data(await call('apply_batch', batch)); assert.equal(replay.replayed, true); assert.equal(replay.revision, committedRevision);
  await edit('undo'); assert.equal(state.revision, compact.revision);
  await edit('redo'); assert.equal(state.revision, committedRevision);
  checks.push('Compact scene, paged drawing query, object detail, batch validation, commit, retry and single undo/redo');
  const preview = await call('preview', { documentId, maxSide: 960 });
  const image = preview.content.find(c => c.type === 'image'); assert(image && image.mimeType === 'image/png');
  fs.writeFileSync(path.join(output, 'ai-edit-preview.png'), Buffer.from(image.data, 'base64'));
  await edit('save_project', { path: path.join(output, 'ai-edit.moruproj') });
  await edit('export_image', { path: path.join(output, 'ai-edit.png') });
  await edit('export_image', { path: path.join(output, 'title.png'), layerId: titleId });
  await call('export_image', { documentId, expectedRevision: state.revision, path: path.join(output, 'ai-edit.png') }, false);
  checks.push('Preview image block, project save, composite/selected-layer export and no-overwrite');
  const request = JSON.stringify({ command: 'get_state', arguments: { documentId } });
  const direct = spawnSync(exe, ['--automation-command', '-', '--session', sessionId], { windowsHide: true, input: request, encoding: 'utf8', timeout: 15000 });
  assert.equal(direct.status, 0, direct.stderr); assert.equal(JSON.parse(direct.stdout).ok, true);
  checks.push('Direct JSON command without MCP');
  const reopened = data(await call('open_document', { path: path.join(output, 'ai-edit.png') }));
  assert.notEqual(reopened.documentId, documentId);
  await call('set_layer', { documentId, expectedRevision: state.revision, layerId: imageId, x: 0 }, false);
  state = data(await call('activate_document', { documentId }));
  await edit('remove_background', { layerId: imageId });
  assert(state.documents.find(d => d.documentId === documentId).layers.find(l => l.layerId === imageId).hasMask);
  await edit('undo');
  checks.push('Image open, inactive-document rejection, activation and local AI background-removal mask');
  // Synthetic parquet tile: no customer drawing or generated-image service required.
  state = data(await call('new_document', { name: 'Synthetic parquet', width: 64, height: 64, background: '#C8AF87' })); documentId = state.documentId;
  for (const [y, color] of [[0, '#AE9069'], [16, '#D8C29D'], [32, '#A98B63'], [48, '#CFB894']])
    await edit('add_shape', { shape: 'rectangle', x: 0, y, width: 64, height: 2, fill: color });
  const texture = path.join(output, 'parquet.png'); await edit('export_image', { path: texture });
  state = data(await call('new_document', { name: 'Material mapping study', width: 960, height: 640, background: '#F5F3EE' })); documentId = state.documentId;
  await edit('add_text', { text: 'MATERIAL STUDY / 01', x: 70, y: 40, fontSize: 28, color: '#29352F', name: 'Sheet title' });
  await edit('add_text', { text: 'EDITABLE BOUNDARIES + ORIGINAL TEXTURES', x: 70, y: 88, fontSize: 14, color: '#647069' });
  await edit('add_shape', { shape: 'rectangle', x: 80, y: 150, width: 600, height: 400, fill: '#FFFFFF', stroke: '#29352F', strokeWidth: 8, name: 'Outer wall' });
  await edit('add_shape', { shape: 'rectangle', x: 380, y: 150, width: 8, height: 400, fill: '#29352F', name: 'Partition' });
  await edit('add_shape', { shape: 'rectangle', x: 180, y: 250, width: 40, height: 40, fill: '#29352F', name: 'Column' });
  await edit('add_shape', { shape: 'rectangle', x: 388, y: 158, width: 284, height: 384, fill: 'transparent', name: 'Closed floor boundary' });
  const boundaryId = state.layerId;
  await edit('add_text', { text: '01 / PARQUET\n64 px repeat\nColumn excluded\n\n02 / ROTATED\n80 px repeat\n90 degrees', x: 722, y: 164, fontSize: 18, lineHeight: 32, color: '#29352F' });
  await edit('register_material', { name: 'Parquet fixture', path: texture, source: 'Synthetic QA fixture', tileable: true });
  const materialId = state.materialId;
  const materials = data(await call('query_materials', { documentId, expectedRevision: state.revision, limit: 1 }));
  assert.equal(materials.materials[0].materialId, materialId);
  const points = [[88,158],[372,158],[372,542],[88,542]].map(([x,y]) => ({ x,y }));
  const holes = [[[180,250],[220,250],[220,290],[180,290]].map(([x,y]) => ({ x,y }))];
  await edit('define_region', { name: 'Left floor', source: 'polygon', points, holes }); const leftRegion = state.regionId;
  await edit('define_region', { name: 'Right floor', source: 'closed_layer', layerId: boundaryId }); const rightRegion = state.regionId;
  const regions = data(await call('query_regions', { documentId, expectedRevision: state.revision, limit: 1 }));
  assert.equal(regions.totalMatches, 2); assert.equal(regions.nextOffset, 1);
  const secondPage = data(await call('query_regions', { documentId, expectedRevision: regions.revision, offset: 1, limit: 1 }));
  assert.equal(secondPage.regions[0].regionId, rightRegion);
  const mapping = { documentId, expectedRevision: state.revision, operationId: randomUUID(), steps: [
    { command: 'apply_material', arguments: { materialId, regionId: leftRegion, tileWidth: 64, tileHeight: 64, name: 'Floor 01' } },
    { command: 'apply_material', arguments: { materialId, regionId: rightRegion, tileWidth: 80, tileHeight: 80, angle: 90, name: 'Floor 02' } }
  ] };
  assert.equal(data(await call('apply_batch', { ...mapping, dryRun: true })).committed, false);
  state = data(await call('apply_batch', mapping)); const fillId = state.steps[1].layerId;
  await edit('update_material', { layerId: fillId, offsetX: 4 });
  const materialDetail = data(await call('get_layer', { documentId, expectedRevision: state.revision, layerId: fillId }));
  assert.equal(materialDetail.layer.material.originalTextureRetained, true);
  assert.equal(materialDetail.layer.material.offsetX, 4);
  const materialPreview = await call('preview', { documentId, maxSide: 960 });
  fs.writeFileSync(path.join(output, 'material-preview.png'), Buffer.from(materialPreview.content.find(c => c.type === 'image').data, 'base64'));
  const project = path.join(output, 'material-study.moruproj'); await edit('save_project', { path: project });
  await edit('export_image', { path: path.join(output, 'material-study.png') });
  checks.push('Material register/query, polygon holes, closed-object region, paged regions, batch mapping, pattern update and preview');
  state = data(await call('open_document', { path: project })); documentId = state.documentId;
  const persisted = data(await call('get_layer', { documentId, expectedRevision: state.revision, layerId: fillId }));
  assert.equal(persisted.layer.material.angle, 90);
  await edit('update_material', { layerId: fillId, angle: 45 }); await edit('undo');
  const restored = data(await call('get_layer', { documentId, expectedRevision: state.revision, layerId: fillId }));
  assert.equal(restored.layer.material.angle, 90);
  checks.push('Native material project reopens with embedded original, editable pattern and undo');
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ ok: true, checks, preview: path.join(output, 'ai-edit-preview.png') }, null, 2));
  console.log(JSON.stringify({ ok: true, checks: checks.length, output }));
}
main().catch(error => { console.error(error.stack, hostErrors, clientErrors); process.exitCode = 1; }).finally(async () => {
  if (mcp) { mcp.stdin.end(); await delay(100); if (mcp.exitCode === null) mcp.kill(); }
  if (host.exitCode === null) host.kill();
  // Both children were started above exclusively for this test. Existing editor sessions are untouched.
});
