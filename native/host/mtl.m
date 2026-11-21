// mtl.m: Metal backend of the D3D9 shim (see mtl.h, METAL-PLAN.md). Encodes the jobs of the batch ring into one
// open command buffer: render passes per (render target, depth) pair, texture uploads as ordered blits, the frame
// copy to the window's front buffer at Present. Fragment shading is an uber-shader that mirrors shade_pixel() in
// d3d9.c (the CPU float path) so both renderers agree.
#import <Metal/Metal.h>
#import <Foundation/Foundation.h>
#include <pthread.h>
#include "mtl.h"
void gw_log(const char *fmt, ...);

static id<MTLDevice> dev; static id<MTLCommandQueue> q; static id<MTLLibrary> lib;
static id<MTLCommandBuffer> cb; static id<MTLRenderCommandEncoder> enc; static id<MTLTexture> enc_rt, enc_ds;
static NSMutableDictionary<NSNumber *, id<MTLRenderPipelineState>> *pipes;
static NSMutableDictionary<NSNumber *, id<MTLDepthStencilState>> *dss;
static NSMutableDictionary<NSNumber *, id<MTLSamplerState>> *samps;
static id<MTLBuffer> vbuf; static size_t vbuf_off;   // vertex arena of the open command buffer
static dispatch_semaphore_t inflight;

static NSString *const SRC = @R"MSL(
#include <metal_stdlib>
using namespace metal;
struct V { float4 pos; float4 c; float4 sp; float2 t[4]; float fog; float pad0, pad1, pad2; };   // 96 bytes, = MtlV
struct Uni { uint st[96]; uint nst, tfactor, specen, fogon, fogtab, fogw, fogcol, atest, afunc, aref, pad0, pad1;
             float fogstart, fogend, fogdens, pad2; float rtw, rth, pad3, pad4; };
struct VO { float4 position [[position]]; float4 c; float4 sp; float2 t0; float2 t1; float2 t2; float2 t3; float fog; float eyew; };
vertex VO vs_main(uint vid [[vertex_id]], const device V *v [[buffer(0)]], constant Uni &u [[buffer(1)]]) {
  V a = v[vid]; float w = a.pos.w != 0.0 ? 1.0 / a.pos.w : 1.0; VO o;
  float nx = (a.pos.x + 0.5) / u.rtw * 2.0 - 1.0, ny = 1.0 - (a.pos.y + 0.5) / u.rth * 2.0;   // D3D9 pixel centres at integers
  o.position = float4(nx * w, ny * w, a.pos.z * w, w);
  o.c = a.c * w; o.sp = a.sp * w; o.t0 = a.t[0] * w; o.t1 = a.t[1] * w; o.t2 = a.t[2] * w; o.t3 = a.t[3] * w; o.fog = a.fog * w; o.eyew = w;
  return o;
}
static float4 unpack(uint c) { return float4(float((c >> 16) & 255u), float((c >> 8) & 255u), float(c & 255u), float(c >> 24)) / 255.0; }
static float4 argv(uint a, float4 diff, float4 spec, float4 cur, float4 tex, float4 tf, float4 temp) {
  float4 o; uint k = a & 15u;
  o = k == 0u ? diff : k == 1u ? cur : k == 2u ? tex : k == 3u ? tf : k == 4u ? spec : k == 5u ? temp : cur;
  if (a & 0x20u) o = float4(o.w);
  if (a & 0x10u) o = 1.0 - o;
  return o;
}
static float4 op4(uint op, float4 a1, float4 a2, float4 a0, float4 diff, float4 tex, float4 tf, float4 cur) {
  switch (op) {
    case 2: return a1; case 3: return a2; case 4: return a1 * a2; case 5: return saturate(a1 * a2 * 2.0); case 6: return saturate(a1 * a2 * 4.0);
    case 7: return saturate(a1 + a2); case 8: return saturate(a1 + a2 - 0.5); case 9: return saturate((a1 + a2 - 0.5) * 2.0); case 10: return saturate(a1 - a2);
    case 11: return saturate(a1 + a2 - a1 * a2); case 12: return a1 * diff.w + a2 * (1.0 - diff.w); case 13: return a1 * tex.w + a2 * (1.0 - tex.w);
    case 14: return a1 * tf.w + a2 * (1.0 - tf.w); case 15: return saturate(a1 + a2 * (1.0 - tex.w)); case 16: return a1 * cur.w + a2 * (1.0 - cur.w);
    case 25: return saturate(a0 + a1 * a2); case 26: return a0 * a1 + (1.0 - a0) * a2;
    default: return a1 * a2;
  }
}
static bool cmpf(uint fn, float a, float b) { switch (fn) { case 1: return false; case 2: return a < b; case 3: return a == b; case 4: return a <= b; case 5: return a > b; case 6: return a != b; case 7: return a >= b; default: return true; } }
static float4 tsample(uint i, float2 uv, texture2d<float> t0, texture2d<float> t1, texture2d<float> t2, texture2d<float> t3,
                      sampler s0, sampler s1, sampler s2, sampler s3) {
  switch (i) { case 0: return t0.sample(s0, uv, level(0)); case 1: return t1.sample(s1, uv, level(0)); case 2: return t2.sample(s2, uv, level(0)); default: return t3.sample(s3, uv, level(0)); }
}
fragment float4 fs_main(VO in [[stage_in]], constant Uni &u [[buffer(1)]],
    texture2d<float> t0 [[texture(0)]], texture2d<float> t1 [[texture(1)]], texture2d<float> t2 [[texture(2)]], texture2d<float> t3 [[texture(3)]],
    sampler s0 [[sampler(0)]], sampler s1 [[sampler(1)]], sampler s2 [[sampler(2)]], sampler s3 [[sampler(3)]]) {
  float4 diff = saturate(in.c), spec = saturate(in.sp), tf = unpack(u.tfactor), cur = diff, temp = float4(0.0);
  float2 tc[4] = {in.t0, in.t1, in.t2, in.t3};
  for (uint s = 0; s < u.nst && s < 4u; s++) {
    uint b = s * 12u; uint cop = u.st[b + 0];
    float4 tex = float4(1.0);
    if (u.st[b + 10]) tex = tsample(s, tc[min(u.st[b + 6], 3u)], t0, t1, t2, t3, s0, s1, s2, s3);   // BGRA8Unorm: sampler returns r,g,b,a
    float4 a1 = argv(u.st[b + 1], diff, spec, cur, tex, tf, temp), a2 = argv(u.st[b + 2], diff, spec, cur, tex, tf, temp), a0 = argv(u.st[b + 7], diff, spec, cur, tex, tf, temp);
    float4 res = op4(cop, a1, a2, a0, diff, tex, tf, cur);
    if (cop == 24u) { float d = 4.0 * dot(a1.xyz - 0.5, a2.xyz - 0.5); res.xyz = float3(saturate(d)); }
    uint aop = u.st[b + 3];
    if (aop == 1u) res.w = cur.w;
    else { float4 b1 = argv(u.st[b + 4], diff, spec, cur, tex, tf, temp), b2 = argv(u.st[b + 5], diff, spec, cur, tex, tf, temp), b0 = argv(u.st[b + 8], diff, spec, cur, tex, tf, temp);
      res.w = op4(aop, b1, b2, b0, diff, tex, tf, cur).w; }
    if (u.st[b + 9] == 5u) temp = res; else cur = res;
  }
  if (u.specen) cur.xyz = saturate(cur.xyz + spec.xyz);
  if (u.fogon) {
    float f;
    if (u.fogtab) { float d = u.fogw ? in.eyew : in.position.z;
      if (u.fogtab == 3u) f = u.fogend != u.fogstart ? (u.fogend - d) / (u.fogend - u.fogstart) : 1.0;
      else if (u.fogtab == 1u) f = exp(-u.fogdens * d); else if (u.fogtab == 2u) { float t = u.fogdens * d; f = exp(-t * t); } else f = 1.0; }
    else f = in.fog;
    f = saturate(f); float3 fc = unpack(u.fogcol).xyz; cur.xyz = f * cur.xyz + (1.0 - f) * fc;
  }
  if (u.atest) { float a = floor(cur.w * 255.0 + 0.5) / 255.0; if (!cmpf(u.afunc, a, float(u.aref & 255u) / 255.0)) discard_fragment(); }
  return saturate(cur);
}
struct CO { float4 position [[position]]; };
vertex CO vs_clear(uint vid [[vertex_id]], constant float4 &zc [[buffer(0)]]) {
  float2 p = float2((vid & 1u) ? 1.0 : -1.0, (vid & 2u) ? 1.0 : -1.0); CO o; o.position = float4(p, zc.x, 1.0); return o; }
fragment float4 fs_clear(CO in [[stage_in]], constant float4 &col [[buffer(0)]]) { return col; }
)MSL";

int mtl_init(void) {
  if (dev) return 1;
  dev = MTLCreateSystemDefaultDevice(); if (!dev) { gw_log("metal: no device"); return 0; }
  q = [dev newCommandQueue]; NSError *e = nil;
  MTLCompileOptions *o = [MTLCompileOptions new]; o.fastMathEnabled = NO;
  lib = [dev newLibraryWithSource:SRC options:o error:&e];
  if (!lib) { gw_log("metal: shader compile failed: %s", e.localizedDescription.UTF8String); dev = nil; return 0; }
  pipes = [NSMutableDictionary dictionary]; dss = [NSMutableDictionary dictionary]; samps = [NSMutableDictionary dictionary];
  inflight = dispatch_semaphore_create(3);
  gw_log("metal: %s ready", dev.name.UTF8String);
  return 1;
}

static void end_enc(void) { if (enc) { [enc endEncoding]; enc = nil; enc_rt = nil; enc_ds = nil; } }
static void need_cb(void) {
  if (cb) return;
  dispatch_semaphore_wait(inflight, DISPATCH_TIME_FOREVER);
  cb = [q commandBuffer]; dispatch_semaphore_t s = inflight;
  [cb addCompletedHandler:^(id<MTLCommandBuffer> c) { (void)c; dispatch_semaphore_signal(s); }];
  vbuf = nil; vbuf_off = 0;
}
static void commit(void) { end_enc(); if (cb) { [cb commit]; cb = nil; } }

void *mtl_texture(void *old, int w, int h, int depth) {
  if (old) { id<MTLTexture> t = (__bridge id<MTLTexture>)old; if ((int)t.width == w && (int)t.height == h) return old; mtl_release(old); }
  MTLTextureDescriptor *d = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:depth ? MTLPixelFormatDepth32Float : MTLPixelFormatBGRA8Unorm width:w height:h mipmapped:NO];
  d.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
  d.storageMode = depth ? MTLStorageModePrivate : MTLStorageModeShared;
  id<MTLTexture> t = [dev newTextureWithDescriptor:d];
  return (void *)CFBridgingRetain(t);
}
void mtl_release(void *tex) { if (tex) CFBridgingRelease(tex); }

void mtl_upload(void *tex, const uint32_t *argb, int w, int h) {
  need_cb(); end_enc();
  id<MTLBuffer> b = [dev newBufferWithBytes:argb length:(size_t)w * h * 4 options:MTLResourceStorageModeShared];
  id<MTLBlitCommandEncoder> bl = [cb blitCommandEncoder];
  [bl copyFromBuffer:b sourceOffset:0 sourceBytesPerRow:(size_t)w * 4 sourceBytesPerImage:(size_t)w * h * 4 sourceSize:MTLSizeMake(w, h, 1)
           toTexture:(__bridge id<MTLTexture>)tex destinationSlice:0 destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
  [bl endEncoding];
}

static void begin_pass(id<MTLTexture> rt, id<MTLTexture> ds) {
  if (enc && enc_rt == rt && enc_ds == ds) return;
  need_cb(); end_enc();
  MTLRenderPassDescriptor *p = [MTLRenderPassDescriptor renderPassDescriptor];
  p.colorAttachments[0].texture = rt; p.colorAttachments[0].loadAction = MTLLoadActionLoad; p.colorAttachments[0].storeAction = MTLStoreActionStore;
  if (ds) { p.depthAttachment.texture = ds; p.depthAttachment.loadAction = MTLLoadActionLoad; p.depthAttachment.storeAction = MTLStoreActionStore; }
  enc = [cb renderCommandEncoderWithDescriptor:p]; enc_rt = rt; enc_ds = ds;
  [enc setDepthClipMode:MTLDepthClipModeClamp];
}
static id<MTLTexture> depth_for(id<MTLTexture> rt, void *ds) {
  if (!ds) return nil; id<MTLTexture> d = (__bridge id<MTLTexture>)ds;
  if (d.width != rt.width || d.height != rt.height) { static int warned; if (!warned++) gw_log("metal: depth %lux%lu != target %lux%lu, depth off", d.width, d.height, rt.width, rt.height); return nil; }
  return d;
}
static MTLBlendFactor bf(uint32_t f) {
  switch (f) { case 1: return MTLBlendFactorZero; case 2: return MTLBlendFactorOne; case 3: return MTLBlendFactorSourceColor; case 4: return MTLBlendFactorOneMinusSourceColor;
    case 5: case 12: return MTLBlendFactorSourceAlpha; case 6: case 13: return MTLBlendFactorOneMinusSourceAlpha; case 7: return MTLBlendFactorDestinationAlpha;
    case 8: return MTLBlendFactorOneMinusDestinationAlpha; case 9: return MTLBlendFactorDestinationColor; case 10: return MTLBlendFactorOneMinusDestinationColor;
    case 11: return MTLBlendFactorSourceAlphaSaturated; case 14: return MTLBlendFactorBlendColor; case 15: return MTLBlendFactorOneMinusBlendColor; }
  return MTLBlendFactorOne;
}
static MTLBlendOperation bo(uint32_t o) { switch (o) { case 2: return MTLBlendOperationSubtract; case 3: return MTLBlendOperationReverseSubtract; case 4: return MTLBlendOperationMin; case 5: return MTLBlendOperationMax; } return MTLBlendOperationAdd; }
static id<MTLRenderPipelineState> pipe_for(const MtlFixed *f, int hasdepth, int clear) {
  uint64_t key = clear ? (1ull << 63) | ((uint64_t)hasdepth << 1) | (f->wmask ? 1 : 0) | ((uint64_t)f->wmask << 4)
    : ((uint64_t)f->blend) | (uint64_t)f->sbf << 1 | (uint64_t)f->dbf << 5 | (uint64_t)f->bop << 9 | (uint64_t)f->sepa << 12 | (uint64_t)f->sba << 13 | (uint64_t)f->dba << 17 | (uint64_t)f->wmask << 21 | (uint64_t)hasdepth << 25;
  NSNumber *k = @(key); id<MTLRenderPipelineState> p = pipes[k]; if (p) return p;
  MTLRenderPipelineDescriptor *d = [MTLRenderPipelineDescriptor new];
  d.vertexFunction = [lib newFunctionWithName:clear ? @"vs_clear" : @"vs_main"]; d.fragmentFunction = [lib newFunctionWithName:clear ? @"fs_clear" : @"fs_main"];
  MTLRenderPipelineColorAttachmentDescriptor *c = d.colorAttachments[0]; c.pixelFormat = MTLPixelFormatBGRA8Unorm;
  uint32_t wm = f->wmask; c.writeMask = (wm & 1 ? MTLColorWriteMaskRed : 0) | (wm & 2 ? MTLColorWriteMaskGreen : 0) | (wm & 4 ? MTLColorWriteMaskBlue : 0) | (wm & 8 ? MTLColorWriteMaskAlpha : 0);
  if (!clear && f->blend) { c.blendingEnabled = YES; c.sourceRGBBlendFactor = bf(f->sbf); c.destinationRGBBlendFactor = bf(f->dbf); c.rgbBlendOperation = bo(f->bop);
    if (f->sepa) { c.sourceAlphaBlendFactor = bf(f->sba); c.destinationAlphaBlendFactor = bf(f->dba); c.alphaBlendOperation = MTLBlendOperationAdd; }
    else { c.sourceAlphaBlendFactor = bf(f->sbf); c.destinationAlphaBlendFactor = bf(f->dbf); c.alphaBlendOperation = bo(f->bop); } }
  if (hasdepth) d.depthAttachmentPixelFormat = MTLPixelFormatDepth32Float;
  NSError *e = nil; p = [dev newRenderPipelineStateWithDescriptor:d error:&e];
  if (!p) { gw_log("metal: pipeline failed: %s", e.localizedDescription.UTF8String); return nil; }
  pipes[k] = p; return p;
}
static id<MTLDepthStencilState> ds_for(uint32_t zen, uint32_t zfunc, uint32_t zwrite) {
  NSNumber *k = @(zen | zfunc << 1 | zwrite << 5); id<MTLDepthStencilState> s = dss[k]; if (s) return s;
  MTLDepthStencilDescriptor *d = [MTLDepthStencilDescriptor new];
  d.depthCompareFunction = zen ? (MTLCompareFunction)(zfunc >= 1 && zfunc <= 8 ? zfunc - 1 : 7) : MTLCompareFunctionAlways;
  d.depthWriteEnabled = zen && zwrite;
  s = [dev newDepthStencilStateWithDescriptor:d]; dss[k] = s; return s;
}
static id<MTLSamplerState> samp_for(uint32_t v) {
  NSNumber *k = @(v); id<MTLSamplerState> s = samps[k]; if (s) return s;
  MTLSamplerDescriptor *d = [MTLSamplerDescriptor new];
  d.minFilter = d.magFilter = (v & 1) ? MTLSamplerMinMagFilterLinear : MTLSamplerMinMagFilterNearest;
  MTLSamplerAddressMode m[4] = {MTLSamplerAddressModeRepeat, MTLSamplerAddressModeRepeat, MTLSamplerAddressModeMirrorRepeat, MTLSamplerAddressModeClampToEdge};
  d.sAddressMode = m[(v >> 4) & 3]; d.tAddressMode = m[(v >> 8) & 3];
  s = [dev newSamplerStateWithDescriptor:d]; samps[k] = s; return s;
}
static void scissor(id<MTLTexture> rt, int x0, int y0, int x1, int y1) {
  if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0; if (x1 > (int)rt.width) x1 = (int)rt.width; if (y1 > (int)rt.height) y1 = (int)rt.height;
  if (x1 <= x0 || y1 <= y0) { x0 = y0 = 0; x1 = y1 = 0; }
  [enc setScissorRect:(MTLScissorRect){(NSUInteger)x0, (NSUInteger)y0, (NSUInteger)(x1 - x0), (NSUInteger)(y1 - y0)}];
}

void mtl_clear(void *rtp, void *dsp, int x0, int y0, int x1, int y1, int fl, uint32_t col, float z) {
  id<MTLTexture> rt = (__bridge id<MTLTexture>)rtp, ds = nil;
  if (!rt) { return; }   // depth-only clear without a colour target is not used by the game
  ds = (fl & 2) ? depth_for(rt, dsp) : nil;
  if (!(fl & 1) && !ds) return;
  if (x1 <= x0 || y1 <= y0) return;
  begin_pass(rt, ds ? ds : enc_rt == rt ? enc_ds : depth_for(rt, dsp));
  MtlFixed f = {0}; f.wmask = (fl & 1) ? 15 : 0;
  id<MTLRenderPipelineState> p = pipe_for(&f, enc_ds != nil, 1); if (!p) return;
  [enc setRenderPipelineState:p];
  [enc setDepthStencilState:ds_for(ds ? 1 : 0, 8, ds ? 1 : 0)];
  [enc setCullMode:MTLCullModeNone];
  scissor(rt, x0, y0, x1, y1);
  float zc[4] = {z, 0, 0, 0}, c[4] = {((col >> 16) & 255) / 255.f, ((col >> 8) & 255) / 255.f, (col & 255) / 255.f, (col >> 24) / 255.f};
  [enc setVertexBytes:zc length:16 atIndex:0]; [enc setFragmentBytes:c length:16 atIndex:0];
  [enc drawPrimitives:MTLPrimitiveTypeTriangleStrip vertexStart:0 vertexCount:4];
}

void mtl_draw(void *rtp, void *dsp, const MtlUni *u, const MtlFixed *f, void *const tex[8], int x0, int y0, int x1, int y1, const MtlV *v, int nv) {
  if (nv <= 0) return;
  id<MTLTexture> rt = (__bridge id<MTLTexture>)rtp; id<MTLTexture> ds = depth_for(rt, dsp);
  begin_pass(rt, ds);
  id<MTLRenderPipelineState> p = pipe_for(f, enc_ds != nil, 0); if (!p) return;
  [enc setRenderPipelineState:p];
  [enc setDepthStencilState:ds_for(f->zen && enc_ds != nil, f->zfunc, f->zwrite)];
  [enc setCullMode:MTLCullModeNone];   // culled on the CPU side (same signed-area rule as the rasteriser)
  if (f->blend) [enc setBlendColorRed:((f->bfactor >> 16) & 255) / 255.f green:((f->bfactor >> 8) & 255) / 255.f blue:(f->bfactor & 255) / 255.f alpha:(f->bfactor >> 24) / 255.f];
  scissor(rt, x0, y0, x1, y1);
  size_t n = (size_t)nv * sizeof(MtlV);
  if (!vbuf || vbuf_off + n > vbuf.length) { size_t cap = n > (8u << 20) ? n : (8u << 20); vbuf = [dev newBufferWithLength:cap options:MTLResourceStorageModeShared]; vbuf_off = 0; }
  memcpy((char *)vbuf.contents + vbuf_off, v, n);
  [enc setVertexBuffer:vbuf offset:vbuf_off atIndex:0]; vbuf_off += (n + 255) & ~(size_t)255;
  [enc setVertexBytes:u length:sizeof *u atIndex:1]; [enc setFragmentBytes:u length:sizeof *u atIndex:1];
  for (int i = 0; i < 4; i++) {
    id<MTLTexture> t = tex[i] ? (__bridge id<MTLTexture>)tex[i] : nil;
    if (t == rt) { static int warned; if (!warned++) gw_log("metal: draw samples its own render target; texture dropped"); t = nil; }
    [enc setFragmentTexture:t atIndex:i]; [enc setFragmentSamplerState:samp_for(f->samp[i]) atIndex:i];
  }
  [enc drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:nv];
}

void mtl_present(void *bb, int w, int h, uint32_t *front, void (*done)(void)) {
  need_cb(); end_enc();
  id<MTLBuffer> b = [dev newBufferWithLength:(size_t)w * h * 4 options:MTLResourceStorageModeShared];
  id<MTLBlitCommandEncoder> bl = [cb blitCommandEncoder];
  [bl copyFromTexture:(__bridge id<MTLTexture>)bb sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
             toBuffer:b destinationOffset:0 destinationBytesPerRow:(size_t)w * 4 destinationBytesPerImage:(size_t)w * h * 4];
  [bl endEncoding];
  [cb addCompletedHandler:^(id<MTLCommandBuffer> c) { (void)c; memcpy(front, b.contents, (size_t)w * h * 4); if (done) done(); }];
  commit();
}

void mtl_finish(void) {
  need_cb(); id<MTLCommandBuffer> c = cb; commit(); [c waitUntilCompleted];
}
void mtl_readback(void *tex, uint8_t *dst, int pitch, int w, int h) {
  [(__bridge id<MTLTexture>)tex getBytes:dst bytesPerRow:pitch fromRegion:MTLRegionMake2D(0, 0, w, h) mipmapLevel:0];
}
// The emulator thread has no autorelease pool: every encode/sync runs inside one so command buffers, encoders and
// descriptors are freed per batch instead of accumulating for the whole session.
void mtl_pool(void (*fn)(void *), void *arg) { @autoreleasepool { fn(arg); } }
