// UDriver native agent: injected into a built Unity IL2CPP player, speaks the same HTTP protocol as com.udriver.agent.
#define _GNU_SOURCE
#include "cJSON.h"
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <direct.h>
typedef SOCKET sock_t;
#define sclose closesocket
#define mkdir(p, m) _mkdir(p)
#define SHUT_WR SD_SEND
#else
#include <arpa/inet.h>
#include <dirent.h>
#include <dlfcn.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
typedef int sock_t;
#define sclose close
#endif
#ifdef __APPLE__
#include <ApplicationServices/ApplicationServices.h>
#include <objc/message.h>
#include <objc/runtime.h>
#include <crt_externs.h>
#include <mach-o/dyld.h>
#include <objc/message.h>
#include <objc/runtime.h>
#endif
#ifdef __linux__
#include <link.h>
#endif

typedef void *P;
typedef uintptr_t GCH;
static void L(const char *fmt, ...) {
    va_list a; va_start(a, fmt);
    fputs("[udriver] ", stderr); vfprintf(stderr, fmt, a); fputc('\n', stderr); fflush(stderr);
    va_end(a);
}

// ---------------- il2cpp exports ----------------

#define APIS(X) \
    X(P, domain_get, (void)) X(P, get_corlib, (void)) X(P, thread_attach, (P)) X(void, thread_detach, (P)) X(P *, domain_get_assemblies, (P, size_t *)) X(P, assembly_get_image, (P)) \
    X(size_t, image_get_class_count, (P)) X(P, image_get_class, (P, size_t)) X(P, class_from_name, (P, const char *, const char *)) \
    X(P, class_get_method_from_name, (P, const char *, int)) X(P, class_get_methods, (P, P *)) X(P, class_get_fields, (P, P *)) \
    X(P, class_get_properties, (P, P *)) X(P, class_get_field_from_name, (P, const char *)) X(P, class_get_property_from_name, (P, const char *)) \
    X(P, class_get_parent, (P)) X(const char *, class_get_name, (P)) X(const char *, class_get_namespace, (P)) X(P, class_get_type, (P)) \
    X(P, class_from_type, (P)) X(P, class_from_system_type, (P)) X(bool, class_is_valuetype, (P)) X(bool, class_is_enum, (P)) \
    X(bool, class_is_interface, (P)) X(int, class_get_flags, (P)) X(P, class_get_element_class, (P)) X(P, class_get_declaring_type, (P)) \
    X(const char *, class_get_assemblyname, (P)) X(bool, class_is_assignable_from, (P, P)) X(int32_t, class_value_size, (P, uint32_t *)) \
    X(const char *, field_get_name, (P)) X(size_t, field_get_offset, (P)) X(P, field_get_type, (P)) X(int, field_get_flags, (P)) \
    X(P, field_get_parent, (P)) X(P, field_get_value_object, (P, P)) X(void, field_set_value, (P, P, void *)) \
    X(void, field_static_get_value, (P, void *)) X(void, field_static_set_value, (P, void *)) X(bool, field_has_attribute, (P, P)) \
    X(const char *, property_get_name, (P)) X(P, property_get_get_method, (P)) X(P, property_get_set_method, (P)) X(P, property_get_parent, (P)) \
    X(const char *, method_get_name, (P)) X(uint32_t, method_get_param_count, (P)) X(P, method_get_param, (P, uint32_t)) \
    X(const char *, method_get_param_name, (P, uint32_t)) X(P, method_get_return_type, (P)) X(uint32_t, method_get_flags, (P, uint32_t *)) \
    X(P, method_get_class, (P)) X(P, object_get_class, (P)) X(P, object_new, (P)) X(void *, object_unbox, (P)) X(P, value_box, (P, void *)) \
    X(P, object_get_virtual_method, (P, P)) X(char *, type_get_name, (P)) X(int, type_get_type, (P)) X(P, type_get_object, (P)) \
    X(bool, type_is_byref, (P)) X(P, runtime_invoke, (P, void *, void **, P *)) X(void, runtime_class_init, (P)) \
    X(P, string_new, (const char *)) X(P, string_new_utf16, (const uint16_t *, int32_t)) X(uint16_t *, string_chars, (P)) \
    X(int32_t, string_length, (P)) X(P, array_new, (P, uintptr_t)) X(uint32_t, array_length, (P)) X(int, array_element_size, (P)) \
    X(void, gc_wbarrier_set_field, (P, void **, P)) X(void, gc_disable, (void)) X(void, gc_enable, (void)) \
    X(int64_t, gc_get_used_size, (void)) X(int64_t, gc_get_heap_size, (void)) X(GCH, gchandle_new, (P, bool)) \
    X(GCH, gchandle_new_weakref, (P, bool)) X(P, gchandle_get_target, (GCH)) X(void, gchandle_free, (GCH)) \
    X(P, resolve_icall, (const char *)) X(void, free, (void *)) X(const char *, image_get_name, (P)) X(bool, method_is_generic, (P)) X(P, class_enum_basetype, (P))
#define X(r, n, a) static r(*il2cpp_##n) a;
APIS(X)
#undef X

static void *ga_handle(void) {
#if defined(__APPLE__)
    for (uint32_t i = 0; i < _dyld_image_count(); i++)
        if (strstr(_dyld_get_image_name(i), "GameAssembly.dylib")) return dlopen(_dyld_get_image_name(i), RTLD_NOLOAD | RTLD_NOW);
    return NULL;
#elif defined(_WIN32)
    return GetModuleHandleA("GameAssembly.dll");
#else
    return dlopen("GameAssembly.so", RTLD_NOLOAD | RTLD_NOW);
#endif
}
static void *sym(void *h, const char *n) {
#ifdef _WIN32
    return (void *)GetProcAddress(h, n);
#else
    return dlsym(h, n);
#endif
}
// The player resolves il2cpp_init through dlsym; wrapping it tells us exactly when the runtime is up.
static atomic_int il2cppReady, initSeen;
static int (*realInit)(const char *);
static int initHook(const char *d) { int r = realInit(d); il2cppReady = 1; return r; }
#ifdef __APPLE__
static void *dlsymHook(void *h, const char *n) {
    void *p = dlsym(h, n);
    if (p && n && !strcmp(n, "il2cpp_init")) { realInit = p; initSeen = 1; return (void *)initHook; }
    return p;
}
__attribute__((used, section("__DATA,__interpose"))) static const struct { void *with, *orig; } interpose[] = {{(void *)dlsymHook, (void *)dlsym}};
#endif
static int load_api(void) {
    void *h = NULL;
    for (int t = 0; !h && t < 1200; t++) if (!(h = ga_handle())) usleep(50000);
    if (!h) { L("GameAssembly is not loaded - is this an IL2CPP player?"); return 0; }
    int missing = 0;
#define X(r, n, a) if (!(il2cpp_##n = sym(h, "il2cpp_" #n))) { L("missing export il2cpp_%s", #n); missing++; }
    APIS(X)
#undef X
    return !missing;
}

// ---------------- scratch memory, strings, errors ----------------

typedef struct Blk { struct Blk *next; max_align_t d[]; } Blk;
static Blk *arena;
static void *A(size_t n) { Blk *b = calloc(1, sizeof *b + n + 1); b->next = arena; arena = b; return b->d; }
static void arena_reset(void) { while (arena) { Blk *n = arena->next; free(arena); arena = n; } }
static char *vF(const char *fmt, va_list a) {
    va_list b; va_copy(b, a);
    int n = vsnprintf(NULL, 0, fmt, a);
    char *s = A(n + 1); vsnprintf(s, n + 1, fmt, b); va_end(b);
    return s;
}
static char *F(const char *fmt, ...) { va_list a; va_start(a, fmt); char *s = vF(fmt, a); va_end(a); return s; }
static char *Sn(const char *s, size_t n) { char *r = A(n + 1); memcpy(r, s, n); r[n] = 0; return r; }

typedef struct { char *p; int n, cap, heap; } Buf;
static void bput(Buf *b, const char *s, int n) {
    if (n < 0) n = (int)strlen(s);
    if (b->n + n + 1 > b->cap) {
        int c = (b->n + n + 1) * 2;
        if (b->heap) b->p = realloc(b->p, c);
        else { char *q = A(c); if (b->n) memcpy(q, b->p, b->n); b->p = q; }
        b->cap = c;
    }
    memcpy(b->p + b->n, s, n); b->n += n; b->p[b->n] = 0;
}
static void bs(Buf *b, const char *s) { bput(b, s ? s : "", -1); }
static void bf(Buf *b, const char *fmt, ...) { va_list a; va_start(a, fmt); bs(b, vF(fmt, a)); va_end(a); }
static char *bstr(Buf *b) { return b->p ? b->p : ""; }

static jmp_buf *jb;
static char *err;
static _Noreturn void fail(const char *fmt, ...) {
    va_list a; va_start(a, fmt); err = vF(fmt, a); va_end(a);
    if (!jb) { L("uncaught: %s", err); abort(); }
    longjmp(*jb, 1);
}
#define TRY(...) ({ jmp_buf *_o = jb, _j; int _e = setjmp(_j); if (!_e) { jb = &_j; __VA_ARGS__; } jb = _o; _e; })

static int ieq(const char *a, const char *b) { return a && b && !strcasecmp(a, b); }
static int starts(const char *s, const char *p) { return s && !strncmp(s, p, strlen(p)); }
static int ends(const char *s, const char *p) { size_t a = strlen(s), b = strlen(p); return a >= b && !strcmp(s + a - b, p); }
static int icontains(const char *s, const char *sub) {
    if (!s || !sub) return 0;
    for (size_t n = strlen(sub); *s; s++) if (!strncasecmp(s, sub, n)) return 1;
    return !*sub;
}
static char *Cut(const char *s, int n) { return !s ? "" : (int)strlen(s) <= n ? (char *)s : F("%.*s...", n, s); }
static char *OneLine(const char *s, int n) {
    Buf b = {0};
    for (; s && *s; s++) if (*s == '\n') bs(&b, "\\n"); else if (*s != '\r') bput(&b, s, 1);
    return Cut(bstr(&b), n);
}
static char *TrimEnd(char *s) { int n = (int)strlen(s); while (n && isspace((unsigned char)s[n - 1])) s[--n] = 0; return s; }
static int wild(const char *p, const char *s) {
    if (!*p) return !*s;
    if (*p == '*') return wild(p + 1, s) || (*s && wild(p, s + 1));
    return *s && (*p == '?' || tolower((unsigned char)*p) == tolower((unsigned char)*s)) && wild(p + 1, s + 1);
}
static int matches(const char *pat, const char *s) { return s && (strpbrk(pat, "*?") ? wild(pat, s) : icontains(s, pat)); }

static char *utf8(const uint16_t *w, int n, int heap) {
    char *o = heap ? malloc(n * 3 + 1) : A(n * 3 + 1), *p = o;
    for (int i = 0; i < n; i++) {
        uint32_t c = w[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < n && w[i + 1] >= 0xDC00 && w[i + 1] < 0xE000) c = 0x10000 + ((c - 0xD800) << 10) + (w[++i] - 0xDC00);
        if (c < 0x80) *p++ = c;
        else if (c < 0x800) { *p++ = 0xC0 | c >> 6; *p++ = 0x80 | (c & 63); }
        else if (c < 0x10000) { *p++ = 0xE0 | c >> 12; *p++ = 0x80 | (c >> 6 & 63); *p++ = 0x80 | (c & 63); }
        else { *p++ = 0xF0 | c >> 18; *p++ = 0x80 | (c >> 12 & 63); *p++ = 0x80 | (c >> 6 & 63); *p++ = 0x80 | (c & 63); }
    }
    *p = 0; return o;
}
static char *cs(P s) { return s ? utf8(il2cpp_string_chars(s), il2cpp_string_length(s), 0) : NULL; }
static P ms(const char *s) { return il2cpp_string_new(s ? s : ""); }

// ---------------- il2cpp helpers ----------------

#define ARR(a) ((char *)(a) + 4 * sizeof(P))
#define ALEN(a) ((a) ? (int)il2cpp_array_length(a) : 0)
#define AT(a, i) (((P *)ARR(a))[i])
typedef struct { void *begin; int32_t length; } Span;
typedef struct { float x, y; } V2;
typedef struct { float x, y, z; } V3;
typedef struct { float x, y, w, h; } Rct;

static P find_class(const char *ns, const char *n) {
    static struct { char *ns, *n; P k; } seen[512];
    static int nseen;
    for (int i = 0; i < nseen; i++) if (!strcmp(seen[i].n, n) && !strcmp(seen[i].ns, ns)) return seen[i].k;
    size_t c; P *as = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &c), k = NULL;
    for (size_t i = 0; i < c && !k; i++) k = il2cpp_class_from_name(il2cpp_assembly_get_image(as[i]), ns, n);
    if (k && nseen < 512) { seen[nseen].ns = strdup(ns); seen[nseen].n = strdup(n); seen[nseen++].k = k; }
    return k;
}
#define CLS(ns, n) find_class(ns, n)
#define NEED(ns, n) ({ P _c = CLS(ns, n); if (!_c) fail("class %s.%s is not in this build", ns, n); _c; })
#define IC(T, name) ((T)({ static P _f; if (!_f && !(_f = il2cpp_resolve_icall(name))) fail("engine function %s is not available in this build", name); _f; }))
static P icall_opt(const char *name) { return il2cpp_resolve_icall(name); }

static P mth(P k, const char *n, int argc) {
    for (; k; k = il2cpp_class_get_parent(k)) { P m = il2cpp_class_get_method_from_name(k, n, argc); if (m) return m; }
    return NULL;
}
static P fld(P k, const char *n) {
    for (; k; k = il2cpp_class_get_parent(k)) { P f = il2cpp_class_get_field_from_name(k, n); if (f) return f; }
    return NULL;
}
static int vtBoxed = 1;
static const char *kname(P k) { return il2cpp_class_get_name(k); }
static char *exmsg(P e) {
    P f = fld(il2cpp_object_get_class(e), "_message"), m = f ? il2cpp_field_get_value_object(f, e) : NULL;
    return F("%s: %s", kname(il2cpp_object_get_class(e)), m ? cs(m) : "");
}
static P inv(P m, P obj, void **args) {
    P exc = NULL;
    if (obj && !vtBoxed && il2cpp_class_is_valuetype(il2cpp_method_get_class(m))) obj = il2cpp_object_unbox(obj);
    P r = il2cpp_runtime_invoke(m, obj, args, &exc);
    if (exc) fail("%s", exmsg(exc));
    return r;
}
static P vmeth(P obj, P m) { P v = il2cpp_object_get_virtual_method(obj, m); return v ? v : m; }
// Calls obj.name(args...) on the most derived override; fails if the method was stripped.
static P call(P obj, const char *name, int argc, void **args) {
    P m = mth(il2cpp_object_get_class(obj), name, argc);
    if (!m) fail("method %s.%s is not in this build", kname(il2cpp_object_get_class(obj)), name);
    return inv(vmeth(obj, m), obj, args);
}
static P scall(P k, const char *name, int argc, void **args) {
    P m = mth(k, name, argc);
    if (!m) fail("method %s.%s is not in this build", kname(k), name);
    return inv(m, NULL, args);
}
static P box(P k, void *v) { return il2cpp_value_box(k, v); }
#define UNBOX(T, o) (*(T *)il2cpp_object_unbox(o))
static P typeobj(P k) { return il2cpp_type_get_object(il2cpp_class_get_type(k)); }
static P G(intptr_t h) { return h ? il2cpp_gchandle_get_target((GCH)h) : NULL; }  // Unity bindings return objects as GC handles
static size_t cachedPtrOff = 16;
static P N(P o) { return o ? *(P *)((char *)o + cachedPtrOff) : NULL; }
static int isU(P o);
static int alive(P o) { return o && N(o); }
static char *spanstr(Span s) {
    char *r = s.begin ? utf8(s.begin, s.length, 0) : "";
    if (s.begin) IC(void (*)(void *), "UnityEngine.Bindings.BindingsAllocator::Free")(s.begin);
    return r;
}
static char *uname(P o) {
    if (!alive(o)) return "null";
    Span s = {0};
    IC(void (*)(P, Span *), "UnityEngine.Object::GetName_Injected")(N(o), &s);
    return spanstr(s);
}
static char *appstr(const char *icall) {
    void (*f)(Span *) = il2cpp_resolve_icall(icall);
    if (!f) fail("engine function %s is not available in this build", icall);
    Span s = {0}; f(&s); return spanstr(s);
}
static P gameObjectOf(P o);
static P transformOf(P o);

static int idOff = -1;
static int64_t eid(P o) {
    if (idOff < 0) idOff = IC(int (*)(void), "UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject")();
    return *(int64_t *)((char *)N(o) + idOff);
}
static P byEid(int64_t id) { return IC(P(*)(int64_t *), "UnityEngine.Object::FindObjectFromInstanceIDThreadSafe_Injected")(&id); }

// ---------------- events ----------------

typedef struct { int64_t seq; int frame; double time; char *cat, *text, *detail; } Ev;
enum { Cap = 20000, MaxGroups = 500 };
static Ev evs[Cap];
static int64_t evNext = 1;
static pthread_mutex_t evLock = PTHREAD_MUTEX_INITIALIZER;
static atomic_int curFrame;
static _Atomic double curTime;
typedef struct { char *key, *type, *top, *msg, *stack; int count, firstFrame, lastFrame; double first, last; } Grp;
static Grp groups[MaxGroups];
static int ngroups, ungrouped;

static int64_t LastSeq(void) { pthread_mutex_lock(&evLock); int64_t s = evNext - 1; pthread_mutex_unlock(&evLock); return s; }
static void emit(const char *cat, const char *text, const char *detail) {
    pthread_mutex_lock(&evLock);
    Ev *e = &evs[evNext % Cap];
    free(e->cat); free(e->text); free(e->detail);
    *e = (Ev){evNext++, curFrame, curTime, strdup(cat), strdup(text ? text : ""), detail ? strdup(detail) : NULL};
    pthread_mutex_unlock(&evLock);
}
static int catMatch(const char *cat, const char *p) {
    size_t n = strlen(p);
    return !strcmp(cat, p) || (strlen(cat) > n && cat[n] == '.' && !strncmp(cat, p, n));
}
static char *topFrame(const char *stack, int heap) {
    for (const char *l = stack; l && *l;) {
        const char *e = strchr(l, '\n'); if (!e) e = l + strlen(l);
        while (l < e && isspace((unsigned char)*l)) l++;
        const char *t = e; while (t > l && isspace((unsigned char)t[-1])) t--;
        if (t > l && !starts(l, "UnityEngine.Debug") && !starts(l, "UnityEngine.Logger") && !starts(l, "Rethrow as"))
            return heap ? strndup(l, t - l) : Sn(l, t - l);
        l = *e ? e + 1 : e;
    }
    return heap ? strdup("") : "";
}
static void group(const char *cat, const char *msg, const char *stack) {
    const char *colon = strchr(msg, ':');
    char *type = !strcmp(cat, "log.exception") && colon && colon > msg && !memchr(msg, ' ', colon - msg) ? strndup(msg, colon - msg) : strdup(cat + 4);
    char *top = topFrame(stack, 1), *key;
    if (*top) asprintf(&key, "%s|%s", type, top);
    else { char *m = strndup(msg, 200); for (char *p = m; *p; p++) if (*p == '\n' || *p == '\r') *p = ' '; asprintf(&key, "%s|%s", type, m); free(m); }
    pthread_mutex_lock(&evLock);
    Grp *g = NULL;
    for (int i = 0; i < ngroups && !g; i++) if (!strcmp(groups[i].key, key)) g = &groups[i];
    if (!g && ngroups < MaxGroups) { g = &groups[ngroups++]; *g = (Grp){key, type, top, 0, 0, 0, curFrame, 0, curTime}; key = type = top = NULL; }
    if (g) { g->count++; g->last = curTime; g->lastFrame = curFrame; free(g->msg); free(g->stack); g->msg = strdup(msg); g->stack = strdup(stack ? stack : ""); }
    else ungrouped++;
    pthread_mutex_unlock(&evLock);
    free(key); free(type); free(top);
}

// Application.logMessageReceivedThreaded without managed code: a LogCallback delegate whose invoke_impl is on_log.
static size_t dImpl, dCode, dMethod;
static GCH prevLog;
static void on_log(P self, P cond, P stack, int type, P method) {
    static const char *names[] = {"error", "assert", "warning", "log", "exception"};
    char cat[32]; snprintf(cat, sizeof cat, "log.%s", type >= 0 && type < 5 ? names[type] : "log");
    char *c = cond ? utf8(il2cpp_string_chars(cond), il2cpp_string_length(cond), 1) : strdup("");
    char *s = stack ? utf8(il2cpp_string_chars(stack), il2cpp_string_length(stack), 1) : strdup("");
    emit(cat, c, *s ? s : NULL);
    if (type == 0 || type == 1 || type == 4) group(cat, c, s);
    free(c); free(s);
    P prev = prevLog ? il2cpp_gchandle_get_target(prevLog) : NULL;
    if (prev) ((void (*)(P, P, P, int, P)) * (P *)((char *)prev + dImpl))(*(P *)((char *)prev + dCode), cond, stack, type, *(P *)((char *)prev + dMethod));
}
static void hook_log(void) {
    P app = CLS("UnityEngine", "Application"), cb = CLS("UnityEngine", "Application/LogCallback"), d = CLS("System", "Delegate");
    if (!cb) { size_t c; P *as = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &c);
        for (size_t i = 0; i < c && !cb; i++) { P im = il2cpp_assembly_get_image(as[i]);
            for (size_t j = 0, n = il2cpp_image_get_class_count(im); j < n && !cb; j++) {
                P k = il2cpp_image_get_class(im, j);
                if (!strcmp(kname(k), "LogCallback") && il2cpp_class_get_declaring_type(k) == app) cb = k; } } }
    P fT = app ? il2cpp_class_get_field_from_name(app, "s_LogCallbackHandlerThreaded") : NULL;
    P fI = d ? fld(d, "invoke_impl") : NULL, fC = d ? fld(d, "method_code") : NULL, fM = d ? fld(d, "method") : NULL;
    P inv_ = cb ? il2cpp_class_get_method_from_name(cb, "Invoke", 3) : NULL;
    P setDef = icall_opt("UnityEngine.Application::SetLogCallbackDefined");
    if (!(cb && fT && fI && fC && fM && inv_ && setDef)) { L("log capture unavailable in this build"); return; }
    dImpl = il2cpp_field_get_offset(fI); dCode = il2cpp_field_get_offset(fC); dMethod = il2cpp_field_get_offset(fM);
    il2cpp_runtime_class_init(app);
    P prev = NULL; il2cpp_field_static_get_value(fT, &prev);
    if (prev) prevLog = il2cpp_gchandle_new(prev, false);
    P del = il2cpp_object_new(cb);
    *(P *)((char *)del + dImpl) = (P)on_log;
    *(P *)((char *)del + dMethod) = inv_;
    il2cpp_field_static_set_value(fT, del);
    ((void (*)(bool))setDef)(true);
}

// ---------------- jobs on the main thread ----------------

typedef struct Job Job;
typedef int (*Step)(void);  // 0 = done, 1 = continue next Update, 2 = continue at end of frame
struct Job {
    const char *tool; cJSON *args; Step fn; int pc, wait;
    Buf txt; cJSON *content; int isError;
    atomic_int cancelled, done, refs;
    char client[64];
    int computer;  // the server said computer access is on
    int64_t i[8]; double d[8]; GCH h[6];
    Job *sub, *next;
};
static Job *J, *queue, *active;
static pthread_mutex_t jobLock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t jobCv = PTHREAD_COND_INITIALIZER;
#define BEGIN switch (J->pc) { case 0:
#define YIELD(ph) do { J->pc = __LINE__; return ph; case __LINE__:; } while (0)
#define END } return 0;

static void Text(const char *fmt, ...) { va_list a; va_start(a, fmt); bs(&J->txt, vF(fmt, a)); bs(&J->txt, "\n"); va_end(a); }
static void Error(const char *s) { J->isError = 1; Text("ERROR: %s", s); }
static void flush(Job *j) {
    if (!j->txt.n) return;
    cJSON *t = cJSON_CreateObject();
    cJSON_AddStringToObject(t, "type", "text"); cJSON_AddStringToObject(t, "text", TrimEnd(j->txt.p));
    cJSON_AddItemToArray(j->content, t);
    j->txt.n = 0;
}
static void Image(const char *b64, const char *mime) {
    flush(J);
    cJSON *t = cJSON_CreateObject();
    cJSON_AddStringToObject(t, "type", "image"); cJSON_AddStringToObject(t, "data", b64); cJSON_AddStringToObject(t, "mimeType", mime);
    cJSON_AddItemToArray(J->content, t);
}
static Job *job_new(const char *tool, cJSON *args, Step fn, int refs) {
    Job *j = calloc(1, sizeof *j);
    j->tool = strdup(tool); j->args = args; j->fn = fn; j->refs = refs; j->txt.heap = 1; j->content = cJSON_CreateArray();
    return j;
}
static void job_unref(Job *j) {
    if (--j->refs > 0) return;
    for (int i = 0; i < 6; i++) if (j->h[i]) il2cpp_gchandle_free(j->h[i]);
    cJSON_Delete(j->args); cJSON_Delete(j->content); free(j->txt.p); free((char *)j->tool); free(j);
}
static P hget(int i) { return J->h[i] ? il2cpp_gchandle_get_target(J->h[i]) : NULL; }
static void hset(int i, P o) { if (J->h[i]) il2cpp_gchandle_free(J->h[i]); J->h[i] = o ? il2cpp_gchandle_new(o, false) : 0; }
static void release_input(Job *j);
static void scene_events(void);

static int runStep(Job *j) {
    Job *o = J; J = j;
    volatile int r = 0;
    if (TRY(r = j->fn())) { Error(err); r = 0; }
    J = o;
    return r;
}
static void finish(Job *j) {
    release_input(j);
    flush(j);
    pthread_mutex_lock(&jobLock); j->done = 1; pthread_cond_broadcast(&jobCv); pthread_mutex_unlock(&jobLock);
    job_unref(j);
}
static double (*rtime)(void);
static int (*frameCount)(void);
static void pump(int phase) {
    il2cpp_gc_disable();
    curFrame = frameCount(); curTime = rtime();
    if (phase == 1) scene_events();
    pthread_mutex_lock(&jobLock);
    Job *list = active; active = NULL;
    while (queue) { Job *j = queue; queue = j->next; j->next = list; list = j; }
    pthread_mutex_unlock(&jobLock);
    Job *keep = NULL;
    while (list) {
        Job *j = list; list = j->next;
        if (!j->cancelled && j->wait && j->wait != phase) { j->next = keep; keep = j; continue; }
        int r = j->cancelled ? 0 : runStep(j);
        if (r) { j->wait = r; j->next = keep; keep = j; } else finish(j);
    }
    pthread_mutex_lock(&jobLock);
    while (keep) { Job *j = keep; keep = j->next; j->next = active; active = j; }
    pthread_mutex_unlock(&jobLock);
    arena_reset();
    il2cpp_gc_enable();
}

// ---------------- player loop hooks ----------------

static int port;
static char token[128];
static int allowUnsafe, shelled;
static atomic_llong lastTick;
static long long now_ms(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1000LL + t.tv_nsec / 1000000; }
static void publish(void);
static int pair(const char *key);
// Unity calls Application.Internal_ApplicationQuit when the game starts to quit; after that the player loop must be left alone.
static atomic_int quitting;
static void (*realQuit)(P);
static void quitHook(P mi) { quitting = 1; realQuit(mi); }
static void main_init(void) {
    P k = CLS("System", "Int32"), m = mth(k, "ToString", 0), exc = NULL; int v = 42;
    P s = m ? il2cpp_runtime_invoke(m, box(k, &v), NULL, &exc) : NULL;
    char *t = s && !exc ? utf8(il2cpp_string_chars(s), il2cpp_string_length(s), 1) : NULL;
    vtBoxed = t && !strcmp(t, "42"); free(t);
    P cp = fld(CLS("UnityEngine", "Object"), "m_CachedPtr");
    if (cp) cachedPtrOff = il2cpp_field_get_offset(cp);
    rtime = il2cpp_resolve_icall("UnityEngine.Time::get_realtimeSinceStartupAsDouble");
    frameCount = il2cpp_resolve_icall("UnityEngine.Time::get_frameCount");
    hook_log();
    P rib = icall_opt("UnityEngine.Application::set_runInBackground");
    if (rib) ((void (*)(bool))rib)(true);
    P q = mth(CLS("UnityEngine", "Application"), "Internal_ApplicationQuit", 0);
    if (q) { realQuit = *(void **)q; *(void **)q = (void *)quitHook; }
    publish();
}
static void on_update(void) {
    static int inited;
    lastTick = now_ms();
    if (!inited) { inited = 1; if (TRY(main_init())) L("init: %s", err); }
    pump(1);
}
static void on_eof(void) { pump(2); }
static void (*updVar)(void) = on_update, (*eofVar)(void) = on_eof;

static P (*GetLoop)(void);
static void (*SetLoop)(P);
static char *loopName(char *e) {
    P rt = *(P *)e;
    if (!rt) return strdup("");
    char *n = il2cpp_type_get_name(((P *)rt)[2]), *r = strdup(n);
    il2cpp_free(n);
    return r;
}
// Adds an entry as the last child of Update and one right after PostLateUpdate.TriggerEndOfFrameCallbacks.
static int loop_insert(void) {
    P cur = GetLoop();
    if (!cur || il2cpp_array_length(cur) < 2) return 0;
    P ak = il2cpp_object_get_class(cur);
    int es = il2cpp_array_element_size(ak), n = il2cpp_array_length(cur), upd = 0, plu = 0, eof = 0;
    char *src = ARR(cur);
#define SUB(a, i) (*(int32_t *)((a) + (i) * es + 32))
    for (int i = 0; i < n; i++) if (*(P *)(src + i * es + 16) == &updVar) return 1;
    for (int i = 1; i < n; i += 1 + SUB(src, i)) {
        char *nm = loopName(src + i * es);
        if (!strcmp(nm, "UnityEngine.PlayerLoop.Update")) upd = i;
        if (!strcmp(nm, "UnityEngine.PlayerLoop.PostLateUpdate")) plu = i;
        free(nm);
    }
    if (!upd || !plu || plu < upd) { L("player loop has no Update/PostLateUpdate phase"); return 0; }
    for (int i = plu + 1; i <= plu + SUB(src, plu); i += 1 + SUB(src, i)) {
        char *nm = loopName(src + i * es);
        if (!strcmp(nm, "UnityEngine.PlayerLoop.PostLateUpdate.TriggerEndOfFrameCallbacks")) eof = i + 1;
        free(nm);
    }
    int a = upd + 1 + SUB(src, upd), b = eof ? eof : plu + 1 + SUB(src, plu);
    P neu = il2cpp_array_new(il2cpp_class_get_element_class(ak), n + 2);
    char *dst = ARR(neu);
    memcpy(dst, src, a * es);
    memcpy(dst + (a + 1) * es, src + a * es, (b - a) * es);
    memcpy(dst + (b + 2) * es, src + b * es, (n - b) * es);
    memset(dst + a * es, 0, es); memset(dst + (b + 1) * es, 0, es);
    *(P *)(dst + a * es + 16) = &updVar;
    *(P *)(dst + (b + 1) * es + 16) = &eofVar;
    SUB(dst, 0) += 2; SUB(dst, upd) += 1; SUB(dst, plu + 1) += 1;
    P t = typeobj(CLS("System", "Object"));
    for (int i = 0; i < n + 2; i++) {
        P *slot = (P *)(dst + i * es), ty = slot[0], dl = slot[1];
        if (i == a || i == b + 1) ty = t;
        il2cpp_gc_wbarrier_set_field(neu, &slot[0], ty);
        il2cpp_gc_wbarrier_set_field(neu, &slot[1], dl);
    }
#undef SUB
    SetLoop(neu);
    return 1;
}

// ---------------- tools ----------------

#include "tools.c"

// ---------------- HTTP ----------------

enum { MaxBody = 4 * 1024 * 1024, MaxActive = 32 };
static atomic_int nActive;
static char *toolsJson[2];  // [0] while computer access is off: no tools that change the game
static struct { char id[65]; Job *j; } calls[256];

static int sameText(const char *a, const char *b) {
    if (!a || !b) return 0;
    size_t n = strlen(a), m = strlen(b); unsigned d = n != m;
    for (size_t i = 0; i < m; i++) d |= (unsigned char)a[i % (n ? n : 1)] ^ (unsigned char)b[i];
    return !d && n == m;
}
static char *jerr(const char *msg) {
    cJSON *o = cJSON_CreateObject();
    cJSON_AddFalseToObject(o, "ok"); cJSON_AddStringToObject(o, "error", msg);
    char *s = cJSON_PrintUnformatted(o); cJSON_Delete(o); return s;
}
static Step find_tool(const char *name, int computer);
static char *do_call(const char *tool, cJSON *args, const char *client, const char *callId, int computer) {
    Step fn = tool ? find_tool(tool, computer) : NULL;
    char m[300];
    if (!fn) { snprintf(m, sizeof m, "unknown tool '%s'", tool ? tool : ""); cJSON_Delete(args); return jerr(m); }
    Job *j = job_new(tool, args, fn, 2);
    snprintf(j->client, sizeof j->client, "%s", client); j->computer = computer;
    int slot = -1;
    pthread_mutex_lock(&jobLock);
    if (callId && *callId && strlen(callId) <= 64) {
        int dup = 0;
        for (int i = 0; i < 256; i++) if (calls[i].j && !strcmp(calls[i].id, callId)) dup = 1;
        for (int i = 0; i < 256 && !dup && slot < 0; i++) if (!calls[i].j) { slot = i; strcpy(calls[i].id, callId); calls[i].j = j; }
    }
    j->next = queue; queue = j;
    cJSON *to = cJSON_GetObjectItem(args, "timeoutMs");
    int ms = cJSON_IsNumber(to) ? (int)fmin(fmax(to->valuedouble, 0), 600000) : 0, wait = ms > 0 ? ms + 2000 : 120000;
    struct timespec dl; clock_gettime(CLOCK_REALTIME, &dl);
    long long ns = dl.tv_nsec + (long long)wait * 1000000; dl.tv_sec += ns / 1000000000; dl.tv_nsec = ns % 1000000000;
    while (!j->done && pthread_cond_timedwait(&jobCv, &jobLock, &dl) != ETIMEDOUT) {}
    char *res = NULL;
    if (!j->done) {
        j->cancelled = 1;
        dl.tv_sec += 2;
        while (!j->done && pthread_cond_timedwait(&jobCv, &jobLock, &dl) != ETIMEDOUT) {}
        snprintf(m, sizeof m, "timeout: '%s' did not finish within %d ms%s", tool, wait,
                 j->done ? " and was stopped; any input it held is released" : "; the game's main thread looks frozen, so it will be stopped as soon as the game runs again");
        res = jerr(m);
    }
    if (slot >= 0) calls[slot].j = NULL;
    pthread_mutex_unlock(&jobLock);
    if (!res && j->cancelled) { snprintf(m, sizeof m, "cancelled: '%s' was stopped by its caller; any input it held is released", tool); res = jerr(m); }
    if (!res) {
        cJSON *o = cJSON_CreateObject();
        cJSON_AddBoolToObject(o, "ok", !j->isError); cJSON_AddBoolToObject(o, "isError", j->isError);
        cJSON_AddItemReferenceToObject(o, "content", j->content);
        res = cJSON_PrintUnformatted(o); cJSON_Delete(o);
    }
    pthread_mutex_lock(&jobLock); job_unref(j); pthread_mutex_unlock(&jobLock);
    return res;
}

typedef struct { char *host, *origin, *tok, *ctype, *client, *call, *computer; long long clen; } Hdrs;
static void respond(sock_t c, int status, const char *body) {
    const char *why = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : status == 404 ? "Not Found"
                    : status == 413 ? "Payload Too Large" : status == 415 ? "Unsupported Media Type" : status == 503 ? "Service Unavailable" : "Internal Server Error";
    char h[256];
    int n = snprintf(h, sizeof h, "HTTP/1.1 %d %s\r\nContent-Type: application/json\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n", status, why, strlen(body));
    send(c, h, n, 0);
    for (size_t off = 0, len = strlen(body); off < len;) { long w = send(c, body + off, len - off, 0); if (w <= 0) break; off += w; }
}
static void *conn(void *arg) {
    sock_t c = (sock_t)(intptr_t)arg;
    char *req = malloc(16385), *body = NULL; int n = 0, hend = -1;
    int status = 200; char *out = NULL;
    bool busy = atomic_fetch_add(&nActive, 1) + 1 > MaxActive;
    while (n < 16384 && hend < 0) {
        long r = recv(c, req + n, 16384 - n, 0);
        if (r <= 0) break;
        n += r; req[n] = 0;
        char *e = strstr(req, "\r\n\r\n"); if (e) hend = (int)(e - req) + 4;
    }
    if (hend < 0) goto done;
    char verb[16] = "", path[1024] = "";
    sscanf(req, "%15s %1023s", verb, path);
    char *q = strchr(path, '?'); if (q) *q = 0;
    Hdrs H = {.clen = -1};
    for (char *l = strstr(req, "\r\n") + 2; l < req + hend - 2;) {
        char *e = strstr(l, "\r\n"); *e = 0;
        char *v = strchr(l, ':');
        if (v) {
            *v++ = 0; while (*v == ' ' || *v == '\t') v++;
            if (!strcasecmp(l, "Host")) H.host = v; else if (!strcasecmp(l, "Origin")) H.origin = v;
            else if (!strcasecmp(l, "X-UDriver-Token")) H.tok = v; else if (!strcasecmp(l, "Content-Type")) H.ctype = v;
            else if (!strcasecmp(l, "X-UDriver-Client")) H.client = v; else if (!strcasecmp(l, "X-UDriver-Call")) H.call = v;
            else if (!strcasecmp(l, "X-UDriver-Computer")) H.computer = v;
            else if (!strcasecmp(l, "Content-Length")) H.clen = atoll(v);
        }
        l = e + 2;
    }
    char hp1[64], hp2[64];
    snprintf(hp1, sizeof hp1, "127.0.0.1:%d", port); snprintf(hp2, sizeof hp2, "localhost:%d", port);
    bool post = !strcmp(verb, "POST"), get = !strcmp(verb, "GET"), computer = H.computer && !strcmp(H.computer, "1");
    if (busy) { status = 503; out = strdup("{\"ok\":false,\"error\":\"too many requests at once\"}"); }
    else if (!H.host || (strcmp(H.host, hp1) && strcmp(H.host, hp2))) { status = 403; out = jerr("unexpected Host header"); }
    else if (H.origin && *H.origin) { status = 403; out = jerr("requests from web pages are not accepted"); }
    else if (!strcmp(path, "/pair") && post) { int r = pair(H.tok); if (r < 0) { status = 400; out = jerr("the key must be 32 to 127 characters"); } else if (!r) { status = 409; out = jerr("already connected to another program"); } else out = strdup("{\"ok\":true}"); }
    else if (strcmp(path, "/ping") && !(*token && sameText(H.tok, token))) { status = 401; out = jerr("missing or wrong X-UDriver-Token"); }
    else if (!strcmp(path, "/ping") && get) asprintf(&out, "{\"ok\":true,\"pid\":%d}", (int)getpid());
    else if (!strcmp(path, "/tools") && get) out = strdup(toolsJson[computer]);
    else if (!strcmp(path, "/cancel") && post && H.clen >= 0 && H.clen <= 1024) {
        body = malloc(H.clen + 1); int have = n - hend < H.clen ? n - hend : (int)H.clen;
        memcpy(body, req + hend, have);
        while (have < H.clen) { long r = recv(c, body + have, H.clen - have, 0); if (r <= 0) break; have += r; }
        body[have] = 0;
        cJSON *o = cJSON_Parse(body), *id = cJSON_GetObjectItem(o, "call");
        bool found = false;
        pthread_mutex_lock(&jobLock);
        for (int i = 0; i < 256 && cJSON_IsString(id); i++) if (calls[i].j && !strcmp(calls[i].id, id->valuestring)) { calls[i].j->cancelled = 1; found = true; }
        pthread_mutex_unlock(&jobLock);
        cJSON_Delete(o);
        if (!o) { status = 500; out = jerr("JsonReaderException: the body is not valid JSON"); }
        else out = strdup(found ? "{\"ok\":true}" : "{\"ok\":false}");
    }
    else if (!strcmp(path, "/call") && post && (!H.ctype || strncasecmp(H.ctype, "application/json", 16))) { status = 415; out = strdup("{\"ok\":false,\"error\":\"Content-Type must be application/json\"}"); }
    else if (!strcmp(path, "/call") && post && (H.clen < 0 || H.clen > MaxBody)) { status = 413; out = strdup("{\"ok\":false,\"error\":\"request body missing its length or larger than 4 MB\"}"); }
    else if (!strcmp(path, "/call") && post) {
        body = malloc(H.clen + 1); long long have = n - hend < H.clen ? n - hend : H.clen;
        memcpy(body, req + hend, have);
        while (have < H.clen) { long r = recv(c, body + have, H.clen - have, 0); if (r <= 0) break; have += r; }
        body[have] = 0;
        cJSON *o = cJSON_Parse(body);
        if (!cJSON_IsObject(o)) { status = 500; out = jerr("JsonReaderException: the body is not a JSON object"); }
        else {
            cJSON *t = cJSON_GetObjectItem(o, "tool"), *a = cJSON_DetachItemFromObject(o, "args");
            if (!cJSON_IsObject(a)) { cJSON_Delete(a); a = cJSON_CreateObject(); }
            char client[48] = "another agent";
            if (H.client && *H.client) {
                int k = 0; for (const char *p = H.client; *p && k < 40; p++) if (*p != '\r') client[k++] = *p == '\n' ? ' ' : *p;
                client[k] = 0; if (strlen(H.client) > 40) strcat(client, "...");
            }
            out = do_call(cJSON_IsString(t) ? t->valuestring : NULL, a, client, H.call, computer);
        }
        cJSON_Delete(o);
    }
    else { status = 404; out = strdup("{\"ok\":false,\"error\":\"not found\"}"); }
    respond(c, status, out);
done:
    shutdown(c, SHUT_WR);
    { char d[8192]; long long total = 0, t0 = now_ms(); long r;
      while (total < 16 << 20 && now_ms() - t0 < 2000 && (r = recv(c, d, sizeof d, 0)) > 0) total += r; }
    sclose(c);
    free(req); free(body); free(out);
    atomic_fetch_sub(&nActive, 1);
    return NULL;
}
static sock_t lsock;
static void *accept_loop(void *_) {
    for (;;) {
        sock_t c = accept(lsock, NULL, NULL);
        if (c < 0) { usleep(100000); continue; }
#ifdef _WIN32
        DWORD tv = 10000; setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, (char *)&tv, sizeof tv);
#else
        struct timeval tv = {10, 0}; setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
#endif
        pthread_t t;
        if (pthread_create(&t, NULL, conn, (void *)(intptr_t)c)) sclose(c); else pthread_detach(t);
    }
    return NULL;
}
static int try_bind(int p) {
    sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in a = {.sin_family = AF_INET, .sin_port = htons(p)};
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
#ifndef _WIN32
    // A restarted game takes its port back while the last run's connections are still in TIME_WAIT.
    int one = 1; setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
#endif
    if (bind(s, (struct sockaddr *)&a, sizeof a) || listen(s, 64)) { sclose(s); return 0; }
    lsock = s; port = p;
    return 1;
}

// ---------------- discovery files ----------------

static char **argv_(int *n) {
#if defined(__APPLE__)
    *n = *_NSGetArgc(); return *_NSGetArgv();
#elif defined(_WIN32)
    *n = __argc; return __argv;
#else
    static char *v[256], buf[65536]; int c = 0;
    FILE *f = fopen("/proc/self/cmdline", "rb"); size_t len = f ? fread(buf, 1, sizeof buf - 1, f) : 0; if (f) fclose(f);
    for (size_t i = 0; i < len && c < 255; i += strlen(buf + i) + 1) v[c++] = buf + i;
    *n = c; return v;
#endif
}
static char *exe_path(void) {
    static char p[4096];
#if defined(__APPLE__)
    uint32_t n = sizeof p; char t[4096];
    if (!_NSGetExecutablePath(t, &n) && realpath(t, p)) return p;
#elif defined(_WIN32)
    GetModuleFileNameA(NULL, p, sizeof p); return p;
#else
    ssize_t n = readlink("/proc/self/exe", p, sizeof p - 1); if (n > 0) { p[n] = 0; return p; }
#endif
    int c; return argv_(&c)[0];
}
static char *dir_file(int p, const char *ext) {
    static char f[2][4096]; static int k; char *r = f[k++ & 1];
    const char *home = getenv("HOME");
#ifdef _WIN32
    if (!home) home = getenv("USERPROFILE");
#endif
    snprintf(r, 4096, "%s/.udriver%s%d%s", home ? home : ".", p ? "/" : "", p, p ? ext : "");
    if (!p) r[strlen(r) - 1] = 0;
    return r;
}
static void write_private(const char *f, const char *text) {
    char tmp[4200]; snprintf(tmp, sizeof tmp, "%s.%d.tmp", f, (int)getpid());
    unlink(tmp);
    int fd = open(tmp, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (fd < 0) { L("cannot write %s: %s", f, strerror(errno)); return; }
    write(fd, text, strlen(text)); close(fd);
    if (rename(tmp, f)) { L("cannot write %s: %s", f, strerror(errno)); unlink(tmp); }
}
static char pubJson[4096];
static void unpublish(void) { if (*pubJson) unlink(pubJson); }
#ifndef _WIN32
static struct sigaction prevSig[32];
static void on_signal(int s) { unpublish(); sigaction(s, &prevSig[s], NULL); raise(s); }
#endif
static void publish(void) {
    char *dir = dir_file(0, "");
    mkdir(dir, 0700); chmod(dir, 0700);
    cJSON *o = cJSON_CreateObject();
    cJSON_AddStringToObject(o, "kind", "game");
    char *volatile name = "", *volatile unity = "";
    TRY(name = appstr("UnityEngine.Application::get_productName_Injected"));
    TRY(unity = appstr("UnityEngine.Application::get_unityVersion_Injected"));
    cJSON_AddStringToObject(o, "name", name);
    cJSON_AddStringToObject(o, "project", exe_path());
    cJSON_AddNumberToObject(o, "pid", getpid());
    cJSON_AddStringToObject(o, "unity", unity);
    cJSON_AddNumberToObject(o, "port", port);
    char *js = cJSON_PrintUnformatted(o); cJSON_Delete(o);
    snprintf(pubJson, sizeof pubJson, "%s", dir_file(port, ".json"));
    if (!shelled) write_private(pubJson, js);  // UDriver wrote it for a game in the shell
    free(js);
    atexit(unpublish);
#ifndef _WIN32
    struct sigaction sa = {.sa_handler = on_signal};
    for (int i = 0, sig[] = {SIGHUP, SIGINT, SIGTERM}; i < 3; i++) sigaction(sig[i], &sa, &prevSig[sig[i]]);
#endif
    L("listening on 127.0.0.1:%d", port);
}

// The first key wins: from app_launch (UDRIVER_TOKEN), or the first POST /pair for a game started by hand (UDriver 2.0 pairing).
static pthread_mutex_t pairLock = PTHREAD_MUTEX_INITIALIZER;
static int pair(const char *key) {
    size_t n = key ? strlen(key) : 0;
    if (n < 32 || n >= sizeof token) return -1;
    pthread_mutex_lock(&pairLock);
    int ok = *token ? sameText(key, token) : (snprintf(token, sizeof token, "%s", key), 1);
    pthread_mutex_unlock(&pairLock);
    return ok;
}

static void build_tools_json(void) {
    static const char manifest[] = {
#embed "tools.json"
        , 0};
    cJSON *all = cJSON_Parse(manifest), *t;
    for (int k = 0; k < 2; k++) {
        cJSON *out = cJSON_CreateArray();
        cJSON_ArrayForEach(t, all) {
            cJSON *n = cJSON_GetObjectItem(t, "name");
            Step fn = cJSON_IsString(n) ? find_tool(n->valuestring, k) : NULL;
            if (fn && fn != t_no_computer) cJSON_AddItemToArray(out, cJSON_Duplicate(t, 1));
        }
        cJSON *o = cJSON_CreateObject();
        cJSON_AddItemToObject(o, "tools", out);
        toolsJson[k] = cJSON_PrintUnformatted(o);
        cJSON_Delete(o);
    }
    cJSON_Delete(all);
}

static void *boot(void *_) {
    if (!load_api()) return NULL;
    // Touching the domain before il2cpp_init finishes creates it on a thread the GC does not know.
    for (int corlib = 0; !il2cppReady && (initSeen || corlib < 50); usleep(20000)) if (il2cpp_get_corlib()) corlib++;
    P self = il2cpp_thread_attach(il2cpp_domain_get());
    GetLoop = il2cpp_resolve_icall("UnityEngine.LowLevel.PlayerLoop::GetCurrentPlayerLoopInternal");
    SetLoop = il2cpp_resolve_icall("UnityEngine.LowLevel.PlayerLoop::SetPlayerLoopInternal");
    if (!GetLoop || !SetLoop) { L("player loop functions not found"); return NULL; }
    int ac, want = 0; char **av = argv_(&ac);
    for (int i = 0; i + 1 < ac; i++) if (!strcmp(av[i], "-udriver-port")) want = atoi(av[i + 1]);
    int ok = want ? try_bind(want) : try_bind(17890);
    for (int p = 17911; !ok && !want && p <= 17930; p++) ok = try_bind(p);
    if (!ok) { L("no free port"); return NULL; }
    pair(getenv("UDRIVER_TOKEN"));
    allowUnsafe = getenv("UDRIVER_UNSAFE") && !strcmp(getenv("UDRIVER_UNSAFE"), "1");
#ifndef _WIN32
    // The operating system keeps a game UDriver started in the computer shell from UDriver's folder named here.
    const char *sh = getenv("UDRIVER_SHELL");
    if (sh && *sh) { DIR *d = opendir(sh); shelled = !d && (errno == EPERM || errno == EACCES); if (d) closedir(d); }
    unsetenv("UDRIVER_SHELL");
#endif
    build_tools_json();
    pthread_t t; pthread_create(&t, NULL, accept_loop, NULL); pthread_detach(t);
    // The player loop array type is cached late in startup; a loaded scene means scripting is fully set up.
    int (*sceneCount)(void) = il2cpp_resolve_icall("UnityEngine.SceneManagement.SceneManager::get_loadedSceneCount");
    while (!sceneCount || sceneCount() < 1) usleep(100000);
    loop_insert();
    il2cpp_thread_detach(self);  // il2cpp_shutdown hangs while a sleeping thread is still attached
    while (!quitting) {  // the game may replace the player loop; put the hooks back
        sleep(1);
        if (quitting || now_ms() - lastTick < 500) continue;
        self = il2cpp_thread_attach(il2cpp_domain_get());
        loop_insert();
        il2cpp_thread_detach(self);
    }
    return NULL;
}

__attribute__((constructor)) static void init(void) {
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2, 2), &w);
#else
    unsetenv("DYLD_INSERT_LIBRARIES");  // UDriver's launch put the agent here; programs the game starts must not get it
    unsetenv("LD_PRELOAD");
#endif
    pthread_t t; pthread_create(&t, NULL, boot, NULL); pthread_detach(t);
}
