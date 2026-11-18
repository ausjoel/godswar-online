// WS2_32 on host BSD sockets. Net.dll (guest) calls these by ordinal (winsock 1.1 numbering).
// SOCKET values handed to the guest are host fd + SOCK_BASE. Every call is logged when GW_NETLOG=1.
#include "gw.h"
#include <sys/socket.h>
#include <sys/ioctl.h>
#include <sys/select.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <fcntl.h>
#include <unistd.h>
#include <errno.h>
#include <poll.h>

#define SOCK_BASE 0x400u
#define INVALID_SOCKET 0xFFFFFFFFu
#define SOCKET_ERROR 0xFFFFFFFFu
static int netlog = -1;
static u32 wsa_err;
static int nl(void) { if (netlog < 0) netlog = getenv("GW_NETLOG") ? atoi(getenv("GW_NETLOG")) : 1; return netlog; }
static int sfd(u32 s) { return s >= SOCK_BASE && s < SOCK_BASE + 65536 ? (int)(s - SOCK_BASE) : -1; }
static u32 map_errno(int e) {
  switch (e) {
    case EWOULDBLOCK: case EINPROGRESS: return 10035;  // WSAEWOULDBLOCK (connect in progress too, like Windows)
    case EALREADY: return 10037; case ENOTSOCK: case EBADF: return 10038; case EMSGSIZE: return 10040;
    case EADDRINUSE: return 10048; case EADDRNOTAVAIL: return 10049; case ENETDOWN: return 10050; case ENETUNREACH: return 10051;
    case ECONNABORTED: return 10053; case ECONNRESET: case EPIPE: return 10054; case ENOBUFS: return 10055; case EISCONN: return 10056;
    case ENOTCONN: return 10057; case ETIMEDOUT: return 10060; case ECONNREFUSED: return 10061; case EHOSTUNREACH: return 10065;
    case EINVAL: return 10022; case EINTR: return 10004; default: return 10022 + 0 * e;
  }
}
static u32 fail(void) { wsa_err = map_errno(errno); set_last_error(wsa_err); return SOCKET_ERROR; }
static int g2h_addr(u32 ga, int glen, struct sockaddr_in *h) {
  memset(h, 0, sizeof *h); if (!ga || glen < 8) return -1;
  h->sin_len = sizeof *h; h->sin_family = AF_INET; memcpy(&h->sin_port, P(ga + 2), 2); memcpy(&h->sin_addr, P(ga + 4), 4); return 0;
}
static void h2g_addr(const struct sockaddr_in *h, u32 ga, u32 glenp) {
  if (!ga) return; u8 b[16] = {0}; b[0] = AF_INET; memcpy(b + 2, &h->sin_port, 2); memcpy(b + 4, &h->sin_addr, 4);
  u32 n = glenp ? r32(glenp) : 16; if (n > 16) n = 16; memcpy(P(ga), b, n); if (glenp) w32(glenp, 16);
}
static const char *astr(const struct sockaddr_in *h) { static char b[4][32]; static int k; char *o = b[k++ & 3];
  snprintf(o, 32, "%s:%u", inet_ntoa(h->sin_addr), ntohs(h->sin_port)); return o; }

SHIM_ORD(WS2_32, 115, WSAStartup, 2) { u32 d = ARG(1); if (d) { memset(P(d), 0, 400); w16(d, 0x0202); w16(d + 2, 0x0202); strcpy((char *)P(d + 4), "gwhost BSD sockets"); strcpy((char *)P(d + 261), "Running"); }
  if (nl()) gw_log("NET WSAStartup(%04x)", ARG(0)); return 0; }
SHIM_ORD(WS2_32, 116, WSACleanup, 0) { return 0; }
SHIM_ORD(WS2_32, 111, WSAGetLastError, 0) { return wsa_err; }
SHIM_ORD(WS2_32, 112, WSASetLastError, 1) { wsa_err = ARG(0); return 0; }
SHIM_ORD(WS2_32, 23, socket, 3) {
  int af = ARG(0), ty = ARG(1), pr = ARG(2);
  int fd = socket(af == 2 ? AF_INET : af, ty == 1 ? SOCK_STREAM : ty == 2 ? SOCK_DGRAM : ty, pr);
  if (fd < 0) { fail(); return INVALID_SOCKET; }
  int one = 1; setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
  if (nl()) gw_log("NET socket(%d,%d,%d) -> %x", af, ty, pr, fd + SOCK_BASE); return fd + SOCK_BASE;
}
SHIM_ORD(WS2_32, 3, closesocket, 1) { int fd = sfd(ARG(0)); if (nl()) gw_log("NET closesocket(%x)", ARG(0)); if (fd < 0 || close(fd) < 0) return fail(); return 0; }
SHIM_ORD(WS2_32, 22, shutdown, 2) { int fd = sfd(ARG(0)); if (shutdown(fd, ARG(1)) < 0) return fail(); return 0; }
SHIM_ORD(WS2_32, 4, connect, 3) {
  int fd = sfd(ARG(0)); struct sockaddr_in a; if (g2h_addr(ARG(1), ARG(2), &a) < 0) { wsa_err = 10014; return SOCKET_ERROR; }
  int r = connect(fd, (struct sockaddr *)&a, sizeof a); int e = errno;
  if (nl()) gw_log("NET connect(%x, %s) -> %d %s", ARG(0), astr(&a), r, r < 0 ? strerror(e) : "");
  if (r < 0) { errno = e; return fail(); } return 0;
}
SHIM_ORD(WS2_32, 2, bind, 3) { int fd = sfd(ARG(0)); struct sockaddr_in a; g2h_addr(ARG(1), ARG(2), &a); if (nl()) gw_log("NET bind(%x, %s)", ARG(0), astr(&a));
  if (bind(fd, (struct sockaddr *)&a, sizeof a) < 0) return fail(); return 0; }
SHIM_ORD(WS2_32, 13, listen, 2) { if (listen(sfd(ARG(0)), ARG(1)) < 0) return fail(); return 0; }
SHIM_ORD(WS2_32, 1, accept, 3) { struct sockaddr_in a; socklen_t l = sizeof a; int fd = accept(sfd(ARG(0)), (struct sockaddr *)&a, &l);
  if (fd < 0) { fail(); return INVALID_SOCKET; } h2g_addr(&a, ARG(1), ARG(2)); return fd + SOCK_BASE; }
SHIM_ORD(WS2_32, 5, getpeername, 3) { struct sockaddr_in a; socklen_t l = sizeof a; if (getpeername(sfd(ARG(0)), (struct sockaddr *)&a, &l) < 0) return fail(); h2g_addr(&a, ARG(1), ARG(2)); return 0; }
SHIM_ORD(WS2_32, 6, getsockname, 3) { struct sockaddr_in a; socklen_t l = sizeof a; if (getsockname(sfd(ARG(0)), (struct sockaddr *)&a, &l) < 0) return fail(); h2g_addr(&a, ARG(1), ARG(2)); return 0; }
static void hexlog(const char *what, u32 s, const u8 *p, int n) {
  if (nl() < 2 || n <= 0) return; char b[3 * 48 + 1]; int k = n < 48 ? n : 48; for (int i = 0; i < k; i++) sprintf(b + 3 * i, "%02x ", p[i]); b[3 * k] = 0;
  gw_log("NET   %s %x [%d] %s%s", what, s, n, b, n > k ? "..." : "");
}
static int peerclosed_logged;
SHIM_ORD(WS2_32, 19, send, 4) {
  int fd = sfd(ARG(0)); ssize_t r = send(fd, P(ARG(1)), ARG(2), 0);
  if (nl()) gw_log("NET send(%x, %u) -> %zd%s", ARG(0), ARG(2), r, r < 0 ? strerror(errno) : "");
  if (r < 0) return fail(); hexlog("tx", ARG(0), P(ARG(1)), (int)r); return (u32)r;
}
SHIM_ORD(WS2_32, 16, recv, 4) {
  int fd = sfd(ARG(0)); int fl = (ARG(3) & 2 ? MSG_PEEK : 0) | (ARG(3) & 1 ? MSG_OOB : 0);
  ssize_t r = recv(fd, P(ARG(1)), ARG(2), fl);
  if (r < 0 && (errno == EWOULDBLOCK || errno == EAGAIN)) { if (nl() >= 3) gw_log("NET recv(%x) would block", ARG(0)); return fail(); }
  if (nl()) gw_log("NET recv(%x, %u) -> %zd%s", ARG(0), ARG(2), r, r < 0 ? strerror(errno) : r == 0 ? " (peer closed)" : "");
  if (r < 0) return fail(); hexlog("rx", ARG(0), P(ARG(1)), (int)r); (void)peerclosed_logged; return (u32)r;
}
SHIM_ORD(WS2_32, 20, sendto, 6) { struct sockaddr_in a; g2h_addr(ARG(4), ARG(5), &a); ssize_t r = sendto(sfd(ARG(0)), P(ARG(1)), ARG(2), 0, (struct sockaddr *)&a, sizeof a); if (r < 0) return fail(); return (u32)r; }
SHIM_ORD(WS2_32, 17, recvfrom, 6) { struct sockaddr_in a; socklen_t l = sizeof a; ssize_t r = recvfrom(sfd(ARG(0)), P(ARG(1)), ARG(2), 0, (struct sockaddr *)&a, &l); if (r < 0) return fail(); h2g_addr(&a, ARG(4), ARG(5)); return (u32)r; }
SHIM_ORD(WS2_32, 10, ioctlsocket, 3) {
  int fd = sfd(ARG(0)); u32 cmd = ARG(1), argp = ARG(2);
  if (cmd == 0x8004667Eu) { int fl = fcntl(fd, F_GETFL); fl = r32(argp) ? fl | O_NONBLOCK : fl & ~O_NONBLOCK; if (fcntl(fd, F_SETFL, fl) < 0) return fail();
    if (nl()) gw_log("NET ioctlsocket(%x, FIONBIO=%u)", ARG(0), r32(argp)); return 0; }
  if (cmd == 0x4004667Fu) { int n = 0; if (ioctl(fd, FIONREAD, &n) < 0) return fail(); w32(argp, n); return 0; }
  gw_log("NET ioctlsocket(%x, %08x) unsupported", ARG(0), cmd); wsa_err = 10045; return SOCKET_ERROR;
}
SHIM_ORD(WS2_32, 21, setsockopt, 5) {
  int fd = sfd(ARG(0)); u32 lv = ARG(1), on = ARG(2), v = ARG(3), len = ARG(4); int iv = len >= 4 ? (int)r32(v) : len ? r8(v) : 0;
  int r = 0;
  if (lv == 0xFFFF) {
    switch (on) {
      case 4: r = setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &iv, sizeof iv); break;
      case 8: r = setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &iv, sizeof iv); break;
      case 0x1001: r = setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &iv, sizeof iv); break;
      case 0x1002: r = setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &iv, sizeof iv); break;
      case 0x80: { struct linger lg = {r16(v), r16(v + 2)}; r = setsockopt(fd, SOL_SOCKET, SO_LINGER, &lg, sizeof lg); break; }
      case 0x1005: case 0x1006: { struct timeval tv = {iv / 1000, (iv % 1000) * 1000}; r = setsockopt(fd, SOL_SOCKET, on == 0x1005 ? SO_SNDTIMEO : SO_RCVTIMEO, &tv, sizeof tv); break; }
      default: break;
    }
  } else if (lv == 6 && on == 1) r = setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &iv, sizeof iv);
  if (nl()) gw_log("NET setsockopt(%x, %x, %x, %d) -> %d", ARG(0), lv, on, iv, r);
  return r < 0 ? fail() : 0;
}
SHIM_ORD(WS2_32, 7, getsockopt, 5) {
  int fd = sfd(ARG(0)); u32 lv = ARG(1), on = ARG(2);
  if (lv == 0xFFFF && on == 0x1007) { int e = 0; socklen_t l = sizeof e; getsockopt(fd, SOL_SOCKET, SO_ERROR, &e, &l); w32(ARG(3), e ? map_errno(e) : 0); if (ARG(4)) w32(ARG(4), 4); return 0; }
  w32(ARG(3), 0); if (ARG(4)) w32(ARG(4), 4); return 0;
}
// Windows fd_set = { u32 count; SOCKET fds[64]; }
static int fdset_in(u32 g, fd_set *h, int *mx) { FD_ZERO(h); if (!g) return 0; u32 n = r32(g); if (n > 64) n = 64;
  for (u32 i = 0; i < n; i++) { int fd = sfd(r32(g + 4 + 4 * i)); if (fd >= 0) { FD_SET(fd, h); if (fd > *mx) *mx = fd; } } return (int)n; }
static void fdset_out(u32 g, fd_set *h) { if (!g) return; u32 n = r32(g), k = 0; if (n > 64) n = 64;
  for (u32 i = 0; i < n; i++) { u32 s = r32(g + 4 + 4 * i); int fd = sfd(s); if (fd >= 0 && FD_ISSET(fd, h)) w32(g + 4 + 4 * k++, s); } w32(g, k); }
SHIM_ORD(WS2_32, 18, select, 5) {
  fd_set r, w, e; int mx = -1; fdset_in(ARG(1), &r, &mx); fdset_in(ARG(2), &w, &mx); fdset_in(ARG(3), &e, &mx);
  struct timeval tv, *tp = NULL; if (ARG(4)) { tv.tv_sec = r32(ARG(4)); tv.tv_usec = r32(ARG(4) + 4); tp = &tv; }
  // Honour the guest's timeout (a connect to a far server needs 250-350 ms; the old 100 ms clamp made the game give up
  // and close the socket before the handshake finished). Only cap very long waits so a stuck call cannot freeze the app.
  if (tp && (tp->tv_sec > 10 || (tp->tv_sec == 10 && tp->tv_usec > 0))) { tv.tv_sec = 10; tv.tv_usec = 0; }
  if (nl() && (ARG(2) || tp)) gw_log("NET select(timeout=%ds %dus, w=%s)", tp ? (int)tp->tv_sec : -1, tp ? (int)tp->tv_usec : 0, ARG(2) ? "yes" : "no");
  int n = select(mx + 1, &r, &w, &e, tp);
  if (n < 0) return fail();
  fdset_out(ARG(1), &r); fdset_out(ARG(2), &w); fdset_out(ARG(3), &e);
  if (nl() >= 3 || (nl() && n)) gw_log("NET select -> %d", n);
  return (u32)n;
}
SHIM_ORD(WS2_32, 151, __WSAFDIsSet, 2) { u32 s = ARG(0), g = ARG(1), n = r32(g); if (n > 64) n = 64; for (u32 i = 0; i < n; i++) if (r32(g + 4 + 4 * i) == s) return 1; return 0; }
SHIM_ORD(WS2_32, 8, htonl, 1) { return htonl(ARG(0)); }
SHIM_ORD(WS2_32, 14, ntohl, 1) { return ntohl(ARG(0)); }
SHIM_ORD(WS2_32, 9, htons, 1) { return htons((u16)ARG(0)); }
SHIM_ORD(WS2_32, 15, ntohs, 1) { return ntohs((u16)ARG(0)); }
SHIM_ORD(WS2_32, 11, inet_addr, 1) { const char *s = (const char *)P(ARG(0)); struct in_addr a; u32 v = inet_aton(s, &a) ? a.s_addr : 0xFFFFFFFFu;
  if (nl()) gw_log("NET inet_addr(%s)", s); return v; }
SHIM_ORD(WS2_32, 12, inet_ntoa, 1) { static u32 buf; if (!buf) buf = heap_alloc(32, 1); struct in_addr a; a.s_addr = ARG(0); snprintf((char *)P(buf), 32, "%s", inet_ntoa(a)); return buf; }
SHIM_ORD(WS2_32, 57, gethostname, 2) { snprintf((char *)P(ARG(0)), ARG(1), "gwhost"); return 0; }
SHIM_ORD(WS2_32, 52, gethostbyname, 1) {
  const char *name = (const char *)P(ARG(0)); struct addrinfo hints = {0}, *res = NULL; hints.ai_family = AF_INET;
  int r = getaddrinfo(name, NULL, &hints, &res);
  if (nl()) gw_log("NET gethostbyname(%s) -> %s", name, r ? gai_strerror(r) : "ok");
  if (r || !res) { wsa_err = 11001; return 0; }
  static u32 he; if (!he) he = heap_alloc(128, 1);
  u32 nm = he + 32, list = he + 16, addr = he + 24;
  snprintf((char *)P(nm), 96, "%s", name);
  memcpy(P(addr), &((struct sockaddr_in *)res->ai_addr)->sin_addr, 4); freeaddrinfo(res);
  w32(list, addr); w32(list + 4, 0);
  w32(he, nm); w32(he + 4, list + 4); w16(he + 8, 2); w16(he + 10, 4); w32(he + 12, list);
  return he;
}
SHIM_ORD(WS2_32, 51, gethostbyaddr, 3) { wsa_err = 11001; return 0; }
