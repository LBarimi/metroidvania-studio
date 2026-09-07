import assert from 'node:assert/strict';

export async function verifyMiniMap(page) {
  const documentBefore = await page.evaluate(async () => JSON.stringify((await (await fetch('/api/state')).json()).document));
  await page.locator('#tabs button').nth(1).click();
  const outline = page.locator('#minimap-outline-width'), entrance = page.locator('#minimap-entrance-length');
  assert.equal(await outline.inputValue(), '9'); assert.equal(await entrance.inputValue(), '15');
  assert.ok(await entrance.getAttribute('aria-label'));
  await outline.selectOption('6'); await entrance.selectOption('25');
  await page.locator('#tabs button').nth(0).click(); await page.locator('#tabs button').nth(1).click();
  assert.equal(await outline.inputValue(), '6'); assert.equal(await entrance.inputValue(), '25');
  await outline.selectOption('9'); await entrance.selectOption('15');
  await page.locator('#tabs button').nth(0).click();

  const result = await page.evaluate(async () => {
    const { MiniMap } = await import('/minimap.js');
    const source = await (await fetch('/api/state')).json();
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:900px;height:650px'; document.body.append(canvas);
    const mini = new MiniMap(canvas, () => {}); mini.setActive(true);
    const dpr = devicePixelRatio, ctx = canvas.getContext('2d');
    let unit = 1, token = 1000;
    function fixture(scale) {
      // Large rooms at tiny zoom keep independent walls far enough apart to measure subpixel coverage.
      unit = Math.max(1, Math.ceil(1 / scale));
      const room = (id, x, y, width, height) => ({ id, name: id, x:x*unit, y:y*unit, width:width*unit, height:height*unit,
        visible:true, locked:false, foreground:[], background:[], objects:[], properties:[{ key:'color', value:'#000000' }] });
      const rooms = [room('a',0,0,20,20), room('b',20,0,8,20), room('c',0,20,20,8), room('d',-10,4,10,4), room('e',32,0,20,20)];
      const connections = [{ roomAId:'a', roomBId:'b', vertical:true, coordinate:20*unit, start:4*unit, end:16*unit },
        { roomAId:'a', roomBId:'c', vertical:false, coordinate:20*unit, start:4*unit, end:16*unit },
        { roomAId:'a', roomBId:'d', vertical:true, coordinate:0, start:4*unit, end:8*unit }];
      mini.setState({ ...source, document:{ ...source.document, rooms }, documentRevision:++token, connections,
        selection:{ ...source.selection, roomId:null } });
      mini.center = { x:21.13*unit, y:12.17*unit }; mini.scale = scale; mini.draw();
    }
    const ink = (x, y) => ctx.getImageData(x, y, 1, 1).data[0] / 255;
    const point = (x, y) => { const p = mini.screen({ x:x*unit, y:y*unit }); return { x:Math.round(p.x*dpr), y:Math.round(p.y*dpr) }; };
    const measure = (x, y, horizontal) => {
      const p = point(x, y); let coverage = 0;
      const radius = Math.ceil(mini.outlineWidth * mini.scale / 5 * dpr) + 2;
      for (let i = -radius; i <= radius; i++) coverage += ink(p.x + (horizontal ? 0 : i), p.y + (horizontal ? i : 0));
      return coverage;
    };
    const samples = [], markers = [], shortSpans = [], openings = [];
    function sample() {
      samples.push({ scale:mini.scale, width:mini.outlineWidth, measured:[measure(10,0,true), measure(42,0,true), measure(0,18,false)] });
    }
    function marker() {
      const verticalSpan=mini.span(mini.state.connections[0]), horizontalSpan=mini.span(mini.state.connections[1]);
      const a={ x:Math.round(verticalSpan.coordinate*dpr), y:Math.round(verticalSpan.start*dpr) };
      const b={ x:Math.round(horizontalSpan.start*dpr), y:Math.round(horizontalSpan.coordinate*dpr) };
      const v = point(0,18), h = point(10,0);
      const limit = Math.floor((verticalSpan.end-verticalSpan.start)*dpr/2); let vertical = 0, horizontal = 0;
      for (const [index,span] of [verticalSpan,horizontalSpan].entries()) {
        const original=mini.state.connections[index];
        openings.push({ scale:mini.scale, extent:span.connection.end-span.connection.start,
          gap:span.connection.end-span.connection.start-2*mini.stub(span)/mini.scale,
          originalExtent:original.end-original.start,
          middle:(span.connection.start+span.connection.end)/2, originalMiddle:(original.start+original.end)/2 });
      }
      for (let i = 0; i < limit; i++) { vertical += ink(a.x, a.y+i); horizontal += ink(b.x+i, b.y); }
      // The sampled center pixel can lie on a fractional trailing edge. Normalize
      // against that orientation's actual wall opacity, not its total band width.
      markers.push({ scale:mini.scale, length:mini.entranceLength, vertical:vertical/ink(v.x,v.y), horizontal:horizontal/ink(h.x,h.y),
        widthPixels:mini.outlineWidth*mini.scale/5*dpr, projected:mini.stub(mini.span(mini.state.connections[0])) });
    }
    const frame = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    try {
      for (const scale of [.05, .1, .2, .5, .75, 1.5, 2, 2.2, 3.75, 5, 8]) {
        fixture(scale); sample(); marker();
      }
      fixture(8);
      for (const length of [7,15,25]) { mini.entranceLength=length; mini.draw(); marker(); }
      for (const width of [1,6,20]) { mini.outlineWidth=width; mini.draw(); sample(); }
      mini.outlineWidth=9; mini.entranceLength=15;
      fixture(5);
      // A fixed narrow doorway must shorten both end markers in map units, never by a fixed screen-pixel gap.
      for (const scale of [.1,.5,1.5,5,8]) {
        mini.scale=scale; mini.draw();
        const span=mini.span(mini.state.connections[2]); shortSpans.push({ scale, length:mini.stub(span) });
      }
      const selections = [];
      for (const scale of [.5, 2, 5]) {
        fixture(scale);
        for (const color of ['#000000','#2080c0']) {
          const rooms = mini.state.document.rooms.map(room => ({ ...room, properties:[{ key:'color', value:color }] }));
          const rgb = (x, y) => { const p=point(x,y); return [...ctx.getImageData(p.x,p.y,1,1).data].slice(0,3); };
          for (const selected of [null,'a','b','e']) {
            mini.setState({ ...mini.state, document:{ ...mini.state.document, rooms }, documentRevision:++token,
              selection:{ ...mini.state.selection, roomId:selected } });
            mini.draw();
            selections.push({ scale, selected, color,
              walls:[rgb(10,0),rgb(42,0),rgb(0,18)],
              entrances:[rgb(20,15),rgb(5,20)],
              interiors:[rgb(8,4),rgb(24,4),rgb(40,4)] });
          }
        }
      }
      fixture(5); const bounds=canvas.getBoundingClientRect();
      const cursor={ x:bounds.width*.63, y:bounds.height*.42 };
      const anchor=mini.world(cursor), before=mini.scale;
      canvas.dispatchEvent(new WheelEvent('wheel',{ deltaY:120, clientX:bounds.left+cursor.x, clientY:bounds.top+cursor.y, cancelable:true }));
      await frame(); sample(); marker(); const after=mini.scale, anchorAfter=mini.world(cursor);
      canvas.dispatchEvent(new WheelEvent('wheel',{ deltaY:-120, clientX:bounds.left+cursor.x, clientY:bounds.top+cursor.y, cancelable:true }));
      await frame(); const restored=mini.scale; sample();
      const large=mini.state.document.rooms.map((room,i) => ({ ...room, x:i*100000000, y:i*10000000 }));
      mini.setState({ ...mini.state, document:{ ...mini.state.document, rooms:large }, documentRevision:++token, connections:[] });
      mini.fit(); mini.draw(); const fitScale=mini.scale;
      canvas.dispatchEvent(new WheelEvent('wheel',{ deltaY:120, clientX:bounds.left+450, clientY:bounds.top+325, cancelable:true }));
      await frame();
      return { dpr,samples,markers,shortSpans,selections,openings,before,after,restored,anchor,anchorAfter,fitScale,afterFitZoom:mini.scale };
    } finally { mini.dispose(); canvas.remove(); }
  });
  // Canvas path coverage is quantized to eighth-pixel scanlines by the rasterizer.
  // Allow that edge coverage error, while rejecting fixed-width or per-room scaling.
  for (const sample of result.samples) {
    const expected=sample.width * sample.scale / 5 * result.dpr;
    for (const measured of sample.measured)
      assert.ok(Math.abs(measured-expected)<.14, `Wall coverage ${measured} vs ${expected} at DPR ${result.dpr}, zoom ${sample.scale}`);
    assert.ok(Math.max(...sample.measured)-Math.min(...sample.measured)<.14, 'All wall orientations and room sizes share one thickness.');
  }
  for (const marker of result.markers) {
    const expected=marker.length * marker.scale / 5 * result.dpr;
    assert.ok(Math.abs(marker.projected*result.dpr-expected)<1e-8, 'Entrance geometry must scale with the map.');
    // A subpixel-wide marker can cover less than one raster sample; its length
    // cannot be reconstructed from an individual pixel, so measure resolvable bars.
    if (marker.widthPixels < 1) continue;
    assert.ok(Math.abs(marker.vertical-expected)<.14, `Vertical marker ${marker.vertical} vs ${expected} at DPR ${result.dpr}, zoom ${marker.scale}`);
    assert.ok(Math.abs(marker.horizontal-expected)<.14, `Horizontal marker ${marker.horizontal} vs ${expected} at DPR ${result.dpr}, zoom ${marker.scale}`);
    assert.ok(Math.abs(marker.horizontal-marker.vertical)<.14, 'Both entrance orientations retain equal length.');
  }
  for (const baseline of result.selections.filter(sample => sample.selected === null)) {
    for (const sample of result.selections.filter(sample => sample.scale === baseline.scale && sample.color === baseline.color)) {
      // Fractional coverage blends with the interior beneath it. Compare identical
      // black underlays at tiny zoom and fully covered colored walls at larger zoom.
      if (sample.color === '#000000' || sample.scale >= 2) {
        assert.deepEqual(sample.walls,baseline.walls,'Room selection must not dim white exterior walls.');
        assert.deepEqual(sample.entrances,baseline.entrances,'Both active and inactive entrances must stay equally white.');
      }
      if (sample.scale >= 2) for (const pixel of [...sample.walls,...sample.entrances])
        assert.deepEqual(pixel,[255,255,255],'Fully covered wall pixels must be pure white.');
      for (const [index,id] of ['a','b','e'].entries()) {
        if (sample.color === '#000000' || !sample.selected || sample.selected === id) assert.deepEqual(sample.interiors[index],baseline.interiors[index]);
        else assert.ok(sample.interiors[index].every((channel,c) => channel < baseline.interiors[index][c]),'Inactive interiors remain dimmed.');
      }
    }
  }
  for (const opening of result.openings) {
    assert.ok(opening.gap > 0 && opening.gap <= 4 + 1e-8,'Wide room adjacencies must leave a compact open passage.');
    assert.ok(opening.extent <= opening.originalExtent && Math.abs(opening.middle-opening.originalMiddle)<1e-8,
      'The visual opening stays centered inside its shared boundary.');
  }
  assert.ok(result.openings.some(opening => opening.extent < opening.originalExtent),'Long boundaries regain wall segments.');
  for (const span of result.shortSpans) assert.ok(Math.abs(span.length/span.scale - 1.9)<1e-8);
  assert.ok(result.after<result.before); assert.ok(Math.abs(result.restored-result.before)<1e-8);
  assert.ok(Math.abs(result.anchor.x-result.anchorAfter.x)<1e-8 && Math.abs(result.anchor.y-result.anchorAfter.y)<1e-8);
  assert.ok(result.fitScale<.002 && result.afterFitZoom<result.fitScale, 'Zooming out after fitting a large world must never jump to a larger scale.');
  const documentAfter = await page.evaluate(async () => JSON.stringify((await (await fetch('/api/state')).json()).document));
  assert.equal(documentAfter,documentBefore);
  return result;
}
