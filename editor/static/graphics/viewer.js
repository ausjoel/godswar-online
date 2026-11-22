// static/graphics/viewer.js - the editor's 3D viewer for GodsWar models (.glb made by gwx.py / tools/graphics).
// ES module, loaded with import() by gw-art.js and modules/graphics.js. three.js r186 is vendored in ./vendor/three.
//
//   const v = await createViewer(container, {height: 420});
//   await v.load(arrayBuffer);           // a .glb
//   v.clips -> ["nomal_stand", ...]; v.play(name); v.setSpeed(1); v.set({wire, grid, skeleton, bg})
//   v.dispose();
// Effects (.gwm): materials carrying extras.gwm_blend with dst = ONE are drawn additively, like the client.
//
// Textures and the editor's Content-Security-Policy: GLTFLoader normally turns each picture embedded in a .glb into
// a blob: URL and fetches it. The live editor is served with "default-src 'self'; img-src 'self' data:", which
// refuses blob: (both as a fetch and as an <img>), so every model came out untextured there while a local editor
// (no policy) looked fine. inlineImages() below decodes the embedded pictures through <img src="data:..."> instead,
// which that policy allows, and hands them to the loader before it tries its own way. Nothing is fetched.
import * as THREE from "./vendor/three/three.module.js";
import { GLTFLoader } from "./vendor/three/GLTFLoader.js";
import { OrbitControls } from "./vendor/three/OrbitControls.js";

function bytesToB64(u8) {
  let bin = "";
  for (let i = 0; i < u8.length; i += 0x8000) bin += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
  return btoa(bin);
}

// GLTFLoader plugin: fill the parser's picture cache from data: URLs (see the note at the top of this file).
// report = {images, failed: [index...]}; a picture that does not decode becomes a 1x1 grey texture, not a failed model.
export function inlineImages(report) {
  return parser => ({
    name: "GW_inline_images",
    beforeRoot() {
      const images = parser.json.images || [];
      report.images = images.length; report.failed = [];
      images.forEach((def, i) => {
        if (def.bufferView === undefined) return;            // an external file: left to the loader
        parser.sourceCache[i] = parser.getDependency("bufferView", def.bufferView).then(buf => new Promise(done => {
          const finish = image => {
            const t = new THREE.Texture(image);
            t.userData.mimeType = def.mimeType || "image/png";
            t.needsUpdate = true;
            const clone = t.clone.bind(t);
            t.clone = () => { const c = clone(); c.needsUpdate = true; return c; };   // a clone starts at version 0
            done(t);
          };
          const img = new Image();
          img.onload = () => finish(img);
          img.onerror = () => {
            report.failed.push(i);
            const c = document.createElement("canvas"); c.width = c.height = 1;
            const g = c.getContext("2d"); g.fillStyle = "#888"; g.fillRect(0, 0, 1, 1);
            finish(c);
          };
          img.src = "data:" + (def.mimeType || "image/png") + ";base64," + bytesToB64(new Uint8Array(buf));
        }));
      });
      return null;
    },
  });
}

export async function createViewer(container, opts) {
  const o = Object.assign({ height: 420, background: 0x0b1a22 }, opts || {});
  const wrap = document.createElement("div");
  wrap.className = "gwv-wrap";
  wrap.style.cssText = "position:relative;width:100%;height:" + o.height + "px;border-radius:4px;overflow:hidden";
  container.append(wrap);
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, preserveDrawingBuffer: true });
  } catch (e) {
    wrap.textContent = "This browser cannot draw 3D (WebGL is off): " + e.message;
    return { load: async () => {}, dispose() {}, clips: [], play() {}, setSpeed() {}, set() {}, snapshot: () => null };
  }
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.setClearColor(o.background, 1);
  wrap.append(renderer.domElement);
  const scene = new THREE.Scene();
  scene.add(new THREE.HemisphereLight(0xffffff, 0x445566, 1.7));
  const sun = new THREE.DirectionalLight(0xffffff, 1.5); sun.position.set(2, 4, 3); scene.add(sun);
  const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  const grid = new THREE.GridHelper(10, 20, 0x2e6a88, 0x16303d); scene.add(grid);
  const clock = new THREE.Clock();
  const state = { root: null, mixer: null, action: null, clips: [], speed: 1, skel: null, wire: false, raf: 0, dead: false, paused: false };

  function resize() {
    const w = wrap.clientWidth || 600, hgt = wrap.clientHeight || o.height;
    renderer.setSize(w, hgt, false);
    renderer.domElement.style.width = "100%"; renderer.domElement.style.height = "100%";
    camera.aspect = w / hgt; camera.updateProjectionMatrix();
  }
  const ro = new ResizeObserver(resize); ro.observe(wrap); resize();
  function tick() {
    if (state.dead) return;
    state.raf = requestAnimationFrame(tick);
    const dt = clock.getDelta();
    if (state.mixer && !state.paused) state.mixer.update(dt * state.speed);
    controls.update();
    renderer.render(scene, camera);
  }
  tick();

  function clear() {
    if (!state.root) return;
    scene.remove(state.root);
    if (state.skel) { scene.remove(state.skel); state.skel = null; }
    state.root.traverse(n => {
      if (!n.isMesh) return;
      n.geometry.dispose();
      for (const m of [].concat(n.material)) { for (const k in m) if (m[k] && m[k].isTexture) m[k].dispose(); m.dispose(); }
    });
    state.root = null; state.mixer = null; state.action = null; state.clips = [];
  }

  function frame() {
    state.root.updateMatrixWorld(true);
    const box = new THREE.Box3(), v = new THREE.Vector3();
    state.root.traverse(n => {
      if (!n.isMesh) return;
      const pos = n.geometry.attributes.position, step = Math.max(1, Math.floor(pos.count / 3000));
      for (let i = 0; i < pos.count; i += step) {
        v.fromBufferAttribute(pos, i);
        if (n.isSkinnedMesh) n.applyBoneTransform(i, v);
        v.applyMatrix4(n.matrixWorld);
        if (isFinite(v.x + v.y + v.z)) box.expandByPoint(v);
      }
    });
    if (box.isEmpty()) box.setFromCenterAndSize(new THREE.Vector3(), new THREE.Vector3(1, 1, 1));
    const c = box.getCenter(new THREE.Vector3()), size = box.getSize(new THREE.Vector3());
    const r = Math.max(0.05, size.length() / 2);
    controls.target.copy(c);
    camera.near = r / 100; camera.far = r * 100; camera.updateProjectionMatrix();
    camera.position.set(c.x + r * 1.1, c.y + r * 0.5, c.z + r * 2.4);
    grid.position.y = box.min.y; grid.scale.setScalar(Math.max(0.2, r / 2.5));
    return { size: [size.x, size.y, size.z] };
  }

  const blendFactor = { 0: THREE.ZeroFactor, 1: THREE.OneFactor, 2: THREE.SrcColorFactor, 3: THREE.OneMinusSrcColorFactor,
    4: THREE.SrcAlphaFactor, 5: THREE.OneMinusSrcAlphaFactor, 6: THREE.DstAlphaFactor, 7: THREE.OneMinusDstAlphaFactor,
    8: THREE.DstColorFactor, 9: THREE.OneMinusDstColorFactor };
  function applyEffectBlend(root) {
    root.traverse(n => {
      if (!n.isMesh) return;
      for (const m of [].concat(n.material)) {
        const b = m.userData && m.userData.gwm_blend;
        m.side = THREE.DoubleSide;
        if (!b) continue;
        m.transparent = true; m.depthWrite = false;
        if (b.dst === 1) { m.blending = THREE.AdditiveBlending; }
        else if (blendFactor[b.src] !== undefined && blendFactor[b.dst] !== undefined) {
          m.blending = THREE.CustomBlending; m.blendSrc = blendFactor[b.src]; m.blendDst = blendFactor[b.dst];
        }
        m.needsUpdate = true;
      }
    });
  }

  const api = {
    THREE, scene, camera, renderer, el: wrap,
    get root() { return state.root; },
    get clips() { return state.clips.map(c => c.name); },
    get textures() { return state.textures || { images: 0, failed: [] }; },   // pictures in the last .glb, and any that failed
    async load(buf) {
      clear();
      const rep = { images: 0, failed: [] };
      const gltf = await new GLTFLoader().register(inlineImages(rep)).parseAsync(buf, "");
      state.textures = rep;
      if (state.dead) return null;
      state.root = gltf.scene; scene.add(state.root);
      state.clips = gltf.animations || [];
      applyEffectBlend(state.root);
      if (state.wire) api.set({ wire: true });
      if (state.clips.length) {
        state.mixer = new THREE.AnimationMixer(state.root);
        const pick = state.clips.find(c => /stand|idle|wait/i.test(c.name)) || state.clips[0];
        api.play(pick.name);
        state.mixer.update(0.01);
      }
      return frame();
    },
    play(name) {
      if (!state.mixer) return;
      const clip = state.clips.find(c => c.name === name);
      if (!clip) return;
      if (state.action) state.action.stop();
      state.action = state.mixer.clipAction(clip); state.action.reset().play();
    },
    pause(on) { state.paused = !!on; },
    setSpeed(s) { state.speed = Number(s) || 1; },
    reframe() { if (state.root) frame(); },
    set(opt) {
      if ("grid" in opt) grid.visible = !!opt.grid;
      if ("bg" in opt) renderer.setClearColor(opt.bg, 1);
      if ("wire" in opt) { state.wire = !!opt.wire; if (state.root) state.root.traverse(n => { if (n.isMesh) for (const m of [].concat(n.material)) m.wireframe = state.wire; }); }
      if ("skeleton" in opt) {
        if (state.skel) { scene.remove(state.skel); state.skel = null; }
        if (opt.skeleton && state.root) { state.skel = new THREE.SkeletonHelper(state.root); scene.add(state.skel); }
      }
    },
    nodes(filter) { const out = []; if (state.root) state.root.traverse(n => { if (!filter || filter(n)) out.push(n); }); return out; },
    snapshot() { renderer.render(scene, camera); return renderer.domElement.toDataURL("image/png"); },
    dispose() { state.dead = true; cancelAnimationFrame(state.raf); ro.disconnect(); clear(); controls.dispose(); renderer.dispose(); wrap.remove(); },
  };
  return api;
}

export function b64ToBuffer(b64) {
  const bin = atob(b64), n = bin.length, out = new Uint8Array(n);
  for (let i = 0; i < n; i++) out[i] = bin.charCodeAt(i);
  return out.buffer;
}
