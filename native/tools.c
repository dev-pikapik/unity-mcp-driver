// Tool implementations, included by udriver.c. Messages match com.udriver.agent word for word.

// ---------------- arguments ----------------

static cJSON *arg(const char *k) { cJSON *t = cJSON_GetObjectItemCaseSensitive(J->args, k); return t && !cJSON_IsNull(t) ? t : NULL; }
static char *jtext(cJSON *t) {
    if (cJSON_IsString(t)) return t->valuestring;
    if (cJSON_IsBool(t)) return cJSON_IsTrue(t) ? "True" : "False";
    if (cJSON_IsNumber(t)) { double d = t->valuedouble; return d == floor(d) && fabs(d) < 1e15 ? F("%lld", (long long)d) : F("%.15g", d); }
    char *s = cJSON_PrintUnformatted(t), *r = F("%s", s); free(s); return r;
}
static char *astr(const char *k, const char *d) { cJSON *t = arg(k); return t ? jtext(t) : (char *)d; }
static double anum(const char *k, double d) {
    cJSON *t = arg(k);
    if (!t) return d;
    if (cJSON_IsNumber(t)) return t->valuedouble;
    if (cJSON_IsBool(t)) return cJSON_IsTrue(t);
    const char *s = cJSON_IsString(t) ? t->valuestring + (t->valuestring[0] == '#') : "";
    char *e; double v = strtod(s, &e);
    if (!cJSON_IsString(t) || e == s || *e) fail("%s must be a number", k);
    return v;
}
static int aint(const char *k, int d) { return (int)rint(anum(k, d)); }
static int abool(const char *k, int d) { cJSON *t = arg(k); return !t ? d : cJSON_IsString(t) ? ieq(t->valuestring, "true") : cJSON_IsTrue(t) || (cJSON_IsNumber(t) && t->valuedouble); }
typedef struct { char **v; int n; } Strs;
static Strs astrs(const char *k) {
    cJSON *t = arg(k), *x; Strs r = {0};
    if (!t) return r;
    if (!cJSON_IsArray(t)) { r.v = A(sizeof(char *)); r.v[r.n++] = jtext(t); return r; }
    r.v = A((cJSON_GetArraySize(t) + 1) * sizeof(char *));
    cJSON_ArrayForEach(x, t) r.v[r.n++] = jtext(x);
    return r;
}
static char *join(Strs s, const char *sep) { Buf b = {0}; for (int i = 0; i < s.n; i++) { if (i) bs(&b, sep); bs(&b, s.v[i]); } return bstr(&b); }

typedef struct { P *v; int n, cap; } Vec;
static void vpush(Vec *a, P x) {
    if (a->n == a->cap) { int c = a->cap ? a->cap * 2 : 16; P *q = A(c * sizeof(P)); if (a->n) memcpy(q, a->v, a->n * sizeof(P)); a->v = q; a->cap = c; }
    a->v[a->n++] = x;
}
// Insertion-ordered string -> int map.
typedef struct { char **k; int *v, *slot, n, cap; } Map;
static int *mget(Map *m, const char *k) {
    if (m->n * 2 >= m->cap) {
        int c = m->cap ? m->cap * 2 : 64; Map o = *m;
        m->k = A(c * sizeof(char *)); m->v = A(c * sizeof(int)); m->slot = A(c * sizeof(int)); m->cap = c; m->n = 0;
        memset(m->slot, -1, c * sizeof(int));
        for (int i = 0; i < o.n; i++) *mget(m, o.k[i]) = o.v[i];
    }
    uint32_t h = 2166136261u; for (const char *p = k; *p; p++) h = (h ^ (unsigned char)*p) * 16777619u;
    for (int i = h % m->cap;; i = (i + 1) % m->cap) {
        if (m->slot[i] < 0) { m->slot[i] = m->n; m->k[m->n] = F("%s", k); m->v[m->n] = 0; return &m->v[m->n++]; }
        if (!strcmp(m->k[m->slot[i]], k)) return &m->v[m->slot[i]];
    }
}

// ---------------- types ----------------

#define K(o) il2cpp_object_get_class(o)
static int isk(P k, P base) { return k && base && il2cpp_class_is_assignable_from(base, k); }
static int is(P o, P base) { return o && isk(K(o), base); }
static int ttype(P t) { return il2cpp_type_get_type(t); }
static int ktype(P k) { return ttype(il2cpp_class_get_type(k)); }
static int prim(P k) { int t = ktype(k); return (t >= 2 && t <= 0xd) || t == 0x18 || t == 0x19; }
static int isvt(P k) { return il2cpp_class_is_valuetype(k); }
static P tk(P type) { return il2cpp_class_from_type(type); }
static const char *nsOf(P k) { P d; while ((d = il2cpp_class_get_declaring_type(k))) k = d; return il2cpp_class_get_namespace(k); }
static char *Full(P k) {
    if (ktype(k) == 0x1d) return F("%s[]", Full(il2cpp_class_get_element_class(k)));
    P d = il2cpp_class_get_declaring_type(k);
    const char *ns = il2cpp_class_get_namespace(k);
    return d ? F("%s+%s", Full(d), kname(k)) : *ns ? F("%s.%s", ns, kname(k)) : (char *)kname(k);
}
static const char *alias(const char *n) {
    static const char *a[][2] = {{"Void", "void"}, {"Int32", "int"}, {"Single", "float"}, {"Boolean", "bool"}, {"String", "string"}, {"Object", "object"}, {"Double", "double"}, {"Int64", "long"}};
    for (int i = 0; i < 8; i++) if (!strcmp(n, a[i][0])) return a[i][1];
    return n;
}
// "System.Collections.Generic.Dictionary`2<System.String,Game.Item>" -> "Dictionary<string,Item>"
static char *tn(P type) {
    if (!type) return "?";
    char *raw = il2cpp_type_get_name(type), *s = F("%s", raw); il2cpp_free(raw);
    Buf b = {0};
    for (char *p = s; *p;) {
        if (strchr("<>,[]", *p)) { bput(&b, p++, 1); continue; }
        if (*p == ' ' || *p == '&') { p++; continue; }
        char *e = p; while (*e && !strchr("<>,[] &", *e)) e++;
        char *w = Sn(p, e - p), *dot = strrchr(w, '.'), *sl = strrchr(w, '/');
        if (sl > dot) dot = sl;
        if (dot) w = dot + 1;
        char *tick = strchr(w, '`'); if (tick) *tick = 0;
        bs(&b, alias(w)); p = e;
    }
    return bstr(&b);
}
static char *TypeName(P k) { return k ? tn(il2cpp_class_get_type(k)) : "?"; }
static int isName(P k, const char *ns, const char *n) { return k && !strcmp(kname(k), n) && !strcmp(il2cpp_class_get_namespace(k), ns); }
static P kUObj(void) { return CLS("UnityEngine", "Object"); }
static int isU(P o) { return is(o, kUObj()); }
static int isEnumerable(P o) { return is(o, CLS("System.Collections", "IEnumerable")); }
static P kTransform(void) { return CLS("UnityEngine", "Transform"); }
static int isString(P o) { return o && K(o) == il2cpp_class_from_type(il2cpp_class_get_type(CLS("System", "String"))); }
static int Simple(P k) {
    if (prim(k) || il2cpp_class_is_enum(k) || ktype(k) == 0xe) return 1;
    if (isName(k, "System", "Decimal") || isName(k, "System", "DateTime") || isName(k, "System", "TimeSpan") || isName(k, "System", "Guid")) return 1;
    return isvt(k) && starts(nsOf(k), "UnityEngine") && !isk(k, CLS("System.Collections", "IEnumerable"));
}
static int asObj(P v) { return !isEnumerable(v) || is(v, kTransform()); }  // shown as an object with members, not as a collection
static char MissingTag;
#define Missing ((P)&MissingTag)

// ---------------- values ----------------

static char *dstr(double f, int dbl) {  // shortest invariant text that reads back as the same value
    if (isnan(f)) return "NaN";
    if (isinf(f)) return f > 0 ? "Infinity" : "-Infinity";
    for (int p = 1; p < 17; p++) { char *s = F("%.*g", p, f); double r = strtod(s, NULL); if (dbl ? r == f : (float)r == (float)f) return s; }
    return F("%.17g", f);
}
static char *fstr(double f) { return dstr(f, 0); }
static char *tostr(P o) {
    int t = ktype(K(o));
    if (t == 0xc || t == 0xd) return dstr(t == 0xc ? UNBOX(float, o) : UNBOX(double, o), t == 0xd);
    return cs(call(o, "ToString", 0, NULL));
}
static P vmI(P o, const char *ns, const char *itf, const char *m, int argc, void **args) {
    P im = mth(CLS(ns, itf), m, argc);
    if (!im) fail("%s.%s is not in this build", itf, m);
    return inv(vmeth(o, im), o, args);
}
static int count(P v) {
    if (ktype(K(v)) == 0x1d) return ALEN(v);
    return UNBOX(int, vmI(v, "System.Collections", "ICollection", "get_Count", 0, NULL));
}
static int isColl(P v) { return ktype(K(v)) == 0x1d || is(v, CLS("System.Collections", "ICollection")); }
static P ielem(P a, int i) {
    P ek = il2cpp_class_get_element_class(K(a));
    return isvt(ek) ? box(ek, ARR(a) + (size_t)i * il2cpp_array_element_size(K(a))) : AT(a, i);
}
typedef struct { int kind, i, n; P src, en, cur, key; } It;
static int it_open(It *it, P v) {
    *it = (It){.src = v, .i = -1};
    if (!v || isString(v)) return 0;
    if (ktype(K(v)) == 0x1d) { it->n = ALEN(v); return 1; }
    if (is(v, CLS("System.Collections", "IDictionary"))) { it->kind = 3; it->en = vmI(v, "System.Collections", "IDictionary", "GetEnumerator", 0, NULL); return 1; }
    if (is(v, CLS("System.Collections", "IList"))) { it->kind = 1; it->n = count(v); return 1; }
    if (!isEnumerable(v)) return 0;
    it->kind = 2; it->en = vmI(v, "System.Collections", "IEnumerable", "GetEnumerator", 0, NULL);
    return 1;
}
static int it_next(It *it) {
    it->i++;
    if (it->kind == 0) { if (it->i >= it->n) return 0; it->cur = ielem(it->src, it->i); return 1; }
    if (it->kind == 1) { if (it->i >= it->n) return 0; int i = it->i; void *a[] = {&i}; it->cur = vmI(it->src, "System.Collections", "IList", "get_Item", 1, a); return 1; }
    if (!it->en || !UNBOX(bool, vmI(it->en, "System.Collections", "IEnumerator", "MoveNext", 0, NULL))) return 0;
    if (it->kind == 3) {
        it->key = vmI(it->en, "System.Collections", "IDictionaryEnumerator", "get_Key", 0, NULL);
        it->cur = vmI(it->en, "System.Collections", "IDictionaryEnumerator", "get_Value", 0, NULL);
    } else it->cur = vmI(it->en, "System.Collections", "IEnumerator", "get_Current", 0, NULL);
    return 1;
}

// ---------------- object ids ----------------

enum { MaxObjIds = 10000 };
static GCH objH[MaxObjIds];
static int objNext;  // ids are 1..objNext; id n lives in objH[(n - 1) % MaxObjIds]
static char *IdOf(P v) {
    if (isU(v)) return alive(v) ? F("#u%lld", (long long)eid(v)) : NULL;
    if (!v || isString(v) || is(v, CLS("System", "Type")) || isvt(K(v))) return NULL;
    // ponytail: linear scan of 10000 weak handles per id lookup; a pointer hash if ids become hot
    for (int n = objNext; n > 0 && n > objNext - MaxObjIds; n--) if (il2cpp_gchandle_get_target(objH[(n - 1) % MaxObjIds]) == v) return F("#o%d", n);
    int n = ++objNext, s = (n - 1) % MaxObjIds;
    if (objH[s]) il2cpp_gchandle_free(objH[s]);
    objH[s] = il2cpp_gchandle_new_weakref(v, false);
    return F("#o%d", n);
}
static P ById(const char *id) {
    char *e; long long n = strlen(id) > 2 ? strtoll(id + 2, &e, 10) : 0;
    int ok = strlen(id) > 2 && !*e;
    if (starts(id, "#u")) {
        P o = ok ? byEid(n) : NULL;
        if (!alive(o)) fail("object %s not found (destroyed?)", id);
        return o;
    }
    if (ok && id[1] == 'o') {
        P t = n > 0 && n <= objNext && n > objNext - MaxObjIds ? il2cpp_gchandle_get_target(objH[(n - 1) % MaxObjIds]) : NULL;
        if (!t) fail("object %s is gone (collected, or older than the newest %d ids) - get a fresh id with state_get", id, MaxObjIds);
        return t;
    }
    fail("bad id '%s': ids look like #u12345 (Unity object) or #o17 (other object)", id);
}

// ---------------- scene objects ----------------

static P tf(P go) { return G(IC(intptr_t (*)(P), "UnityEngine.GameObject::get_transform_Injected")(N(go))); }
static P goOfC(P c) { return G(IC(intptr_t (*)(P), "UnityEngine.Component::get_gameObject_Injected")(N(c))); }
static P gameObjectOf(P o) { return !alive(o) ? NULL : is(o, CLS("UnityEngine", "GameObject")) ? o : is(o, CLS("UnityEngine", "Component")) ? goOfC(o) : NULL; }
static P transformOf(P o) { P g = gameObjectOf(o); return g ? tf(g) : NULL; }
static int nchild(P t) { return IC(int (*)(P), "UnityEngine.Transform::get_childCount_Injected")(N(t)); }
static P child(P t, int i) { return G(IC(intptr_t (*)(P, int), "UnityEngine.Transform::GetChild_Injected")(N(t), i)); }
static P parentT(P t) { return G(IC(intptr_t (*)(P), "UnityEngine.Transform::GetParent_Injected")(N(t))); }
static int activeH(P go) { return IC(bool (*)(P), "UnityEngine.GameObject::get_activeInHierarchy_Injected")(N(go)); }
static P compsK(P go, P k, int recursive) { return IC(P(*)(P, P, bool, bool, bool, bool, P), "UnityEngine.GameObject::GetComponentsInternal_Injected")(N(go), typeobj(k), true, recursive, !recursive, false, NULL); }
static P comps(P go) { return compsK(go, CLS("UnityEngine", "Component"), 0); }
static P comp(P go, P k) { return k && go ? G(IC(intptr_t (*)(P, P), "UnityEngine.GameObject::GetComponent_Injected")(N(go), typeobj(k))) : NULL; }
static P compUp(P go, P k) { return k && go ? G(IC(intptr_t (*)(P, P, bool), "UnityEngine.GameObject::GetComponentInParent_Injected")(N(go), typeobj(k), false)) : NULL; }
static P compDown(P go, P k) { return k && go ? G(IC(intptr_t (*)(P, P, bool), "UnityEngine.GameObject::GetComponentInChildren_Injected")(N(go), typeobj(k), false)) : NULL; }
static V3 position(P t) { V3 v = {0}; IC(void (*)(P, V3 *), "UnityEngine.Transform::get_position_Injected")(N(t), &v); return v; }
static P mget0(P o, const char *getter) { return call(o, getter, 0, NULL); }
static int bget(P o, const char *getter) { return UNBOX(bool, mget0(o, getter)); }
static int iget(P o, const char *getter) { return UNBOX(int, mget0(o, getter)); }
static int sint(const char *ns, const char *k, const char *getter) { return UNBOX(int, scall(NEED(ns, k), getter, 0, NULL)); }
static int ScreenW(void) { return sint("UnityEngine", "Screen", "get_width"); }
static int ScreenH(void) { return sint("UnityEngine", "Screen", "get_height"); }
static char *v3s(V3 v) { return tostr(box(NEED("UnityEngine", "Vector3"), &v)); }

typedef struct { uint64_t s[8]; } Scene;
static int sceneCount(void) { return IC(int (*)(void), "UnityEngine.SceneManagement.SceneManager::get_sceneCount")(); }
static Scene sceneAt(int i) { Scene s = {0}; IC(void (*)(int, Scene *), "UnityEngine.SceneManagement.SceneManager::GetSceneAt_Injected")(i, &s); return s; }
static char *sceneName(Scene *s) { Span sp = {0}; IC(void (*)(Scene *, Span *), "UnityEngine.SceneManagement.Scene::GetNameInternal_Injected")(s, &sp); return spanstr(sp); }
static int sceneLoaded(Scene *s) { return IC(bool (*)(Scene *), "UnityEngine.SceneManagement.Scene::GetIsLoadedInternal_Injected")(s); }
static Vec RootObjects(void) {
    Vec r = {0};
    for (int i = 0, n = sceneCount(); i < n; i++) {
        Scene s = sceneAt(i);
        if (!sceneLoaded(&s)) continue;
        int c = IC(int (*)(Scene *), "UnityEngine.SceneManagement.Scene::GetRootCountInternal_Injected")(&s);
        P arr = il2cpp_array_new(NEED("UnityEngine", "GameObject"), c);
        IC(void (*)(Scene *, P), "UnityEngine.SceneManagement.Scene::GetRootGameObjectsInternalArray_Injected")(&s, arr);
        for (int j = 0; j < c; j++) if (AT(arr, j)) vpush(&r, AT(arr, j));
    }
    return r;
}
static char *PathOf(P go) {
    char *p = "";
    for (P t = tf(go); t; t = parentT(t)) p = F("/%s%s", uname(t), p);
    return p;
}
static P FindGo(const char *path) {
    if (starts(path, "#u")) {
        P o = ById(path), go = gameObjectOf(o);
        if (!go) fail("%s is a %s, not a GameObject", path, kname(K(o)));
        return go;
    }
    while (*path == '/') path++;
    char *s = F("%s", path); TrimEnd(s);
    for (int n = (int)strlen(s); n && s[n - 1] == '/';) s[--n] = 0;
    Vec cur = {0}, roots = RootObjects();
    char *part = strsep(&s, "/");
    for (int i = 0; i < roots.n; i++) if (!strcmp(uname(roots.v[i]), part)) vpush(&cur, tf(roots.v[i]));
    while (s && cur.n) {
        part = strsep(&s, "/");
        Vec next = {0};
        for (int i = 0; i < cur.n; i++) for (int c = 0, m = nchild(cur.v[i]); c < m; c++) { P ch = child(cur.v[i], c); if (!strcmp(uname(ch), part)) vpush(&next, ch); }
        cur = next;
    }
    if (cur.n <= 1) return cur.n ? goOfC(cur.v[0]) : NULL;
    Buf b = {0};
    bf(&b, "'%s' matches %d GameObjects - address one by its id:", path, cur.n);
    for (int i = 0; i < cur.n && i < 20; i++) { P g = goOfC(cur.v[i]); bf(&b, "\n  %s %s", PathOf(g), IdOf(g)); }
    if (cur.n > 20) bf(&b, "\n  ... and %d more", cur.n - 20);
    fail("%s", bstr(&b));
}
static P FindComponent(P go, const char *name, const char *where) {
    P cs_ = comps(go);
    for (int i = 0; i < ALEN(cs_); i++) { P c = AT(cs_, i); if (alive(c) && (!strcmp(kname(K(c)), name) || !strcmp(Full(K(c)), name))) return c; }
    fail("component '%s' not found on %s", name, where);
}

// ---------------- safety policy ----------------

static const char *EnginePrefixes[] = {"System", "mscorlib", "netstandard", "Unity", "Mono.", "Newtonsoft", "UDriver", "0Harmony", "nunit", "Bee.", "ExCSS", "ReportGenerator", 0};
static const char *SafeNs[] = {"System.Collections", "System.Linq", "System.Text", "System.Numerics", 0};
static const char *SafeTypes[] = {"System.Object", "System.String", "System.Math", "System.MathF", "System.Convert", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
                                  "System.Guid", "System.Array", "System.Enum", "System.ValueType", "System.Decimal", "System.Random", "System.Version", "System.BitConverter", 0};
static const char *AllowedUnity[] = {"UnityEngine.UI", "UnityEngine.UIElements", "UnityEngine.EventSystems", "UnityEngine.InputSystem", "UnityEngine.SceneManagement", "UnityEngine.Rendering",
                                     "UnityEngine.AI", "UnityEngine.Animations", "UnityEngine.Audio", "UnityEngine.Tilemaps", "UnityEngine.TextCore", "UnityEngine.U2D", "UnityEngine.Playables",
                                     "UnityEngine.Timeline", "UnityEngine.Events", "UnityEngine.Pool", "UnityEngine.Serialization", "UnityEngine.Splines",
                                     "Unity.Mathematics", "Unity.Collections", "Unity.Cinemachine", "Unity.AI.Navigation", "TMPro", 0};
static const char *DeniedUnity[] = {
    "UnityEngine.Networking", "UnityEngine.Windows", "UnityEngine.AndroidJava", "UnityEngine.Android", "UnityEngine.iOS", "UnityEngine.Apple", "UnityEngine.WSA",
    "UnityEngine.Microphone", "UnityEngine.WebCam", "UnityEngine.Application.OpenURL", "UnityEngine.Application.Unload", "UnityEngine.Diagnostics", "UnityEngine.CrashReport",
    "UnityEngine.Application.Quit", "UnityEngine.Application.RequestUserAuthorization", "UnityEngine.Handheld", "UnityEngine.GUIUtility.systemCopyBuffer",
    "UnityEngine.PlayerPrefs", "Unity.IO.", "Unity.Loading.", "Unity.Content.",
    "Unity.Collections.LowLevel.Unsafe", "Unity.Jobs.LowLevel.Unsafe", "Unity.Burst.",
    "UnityEngine.ScreenCapture", "UnityEngine.Profiling", "UnityEngine.AssetBundle", "UnityEngine.Caching", "UnityEngine.Video", "UnityEngine.WWW",
    "UnityEngine.Windows.File", "UnityEngine.Windows.Directory", "UnityEngine.ImageConversion", "UnityEngine.Experimental", 0};
static int anyStarts(const char *s, const char **l) { for (; *l; l++) if (starts(s, *l)) return 1; return 0; }
static int inNs(const char *ns, const char **l) { for (; *l; l++) if (!strcmp(ns, *l) || (starts(ns, *l) && ns[strlen(*l)] == '.')) return 1; return 0; }
static int IsEngine(P k) { const char *a = il2cpp_class_get_assemblyname(k); return a && anyStarts(a, EnginePrefixes); }
static const char *Why(P k, const char *member) {
    if (allowUnsafe || !k) return NULL;
    const char *ns = nsOf(k); char *full = Full(k);
    if (!strcmp(ns, "UDriver") || starts(ns, "UDriver.")) return "driver internals";
    if (!IsEngine(k)) return NULL;
    if (starts(ns, "UnityEditor")) return "UnityEditor API (use the editor_* tools)";
    if (!strcmp(ns, "UnityEngine") || starts(ns, "UnityEngine.") || starts(ns, "Unity.") || !strcmp(ns, "TMPro")) {
        if (anyStarts(F("%s.%s", full, member), DeniedUnity)) return "Unity API that reaches outside the game (files, network, devices, OS)";
        if (!strcmp(ns, "UnityEngine") || inNs(ns, AllowedUnity)) return NULL;
        return F("Unity namespace outside the game-play list (%s)", ns);
    }
    int safe = 0;
    for (const char **t = SafeTypes; *t && !safe; t++) safe = !strcmp(full, *t);
    if (prim(k) || il2cpp_class_is_enum(k) || safe || starts(full, "System.Tuple") || starts(full, "System.ValueTuple") || starts(full, "System.Nullable")) return NULL;
    if (starts(ns, "System.Linq.Expressions")) return "code generation API";
    if (inNs(ns, SafeNs)) return NULL;
    return "system API outside the safe list (files, processes, network, environment, reflection, threading...)";
}
static void Guard(P k, const char *name, const char *op) {
    const char *why = Why(k, name);
    if (why) fail("%s on %s.%s is blocked by the safety policy: %s. Only a human can lift this, by starting the game/editor with environment variable UDRIVER_UNSAFE=1.", op, Full(k), name, why);
}

// ---------------- members ----------------

static P pType(P p) {  // property type
    P g = il2cpp_property_get_get_method(p);
    return g ? il2cpp_method_get_return_type(g) : il2cpp_method_get_param(il2cpp_property_get_set_method(p), 0);
}
static int mstatic(P m) { uint32_t i; return il2cpp_method_get_flags(m, &i) & 0x10; }
static P fget(P f, P obj) {
    int fl = il2cpp_field_get_flags(f);
    if (!(fl & 0x50)) return il2cpp_field_get_value_object(f, obj);
    P k = tk(il2cpp_field_get_type(f));
    il2cpp_runtime_class_init(il2cpp_field_get_parent(f));
    if (!isvt(k)) { P v = NULL; il2cpp_field_static_get_value(f, &v); return v; }
    void *buf = A(il2cpp_class_value_size(k, NULL) + 16);
    il2cpp_field_static_get_value(f, buf);
    return box(k, buf);
}
static P pget(P m, P obj) { return mstatic(m) ? inv(m, NULL, NULL) : inv(vmeth(obj, m), obj, NULL); }
static P getter(P p) { P g = il2cpp_property_get_get_method(p); return g && il2cpp_method_get_param_count(g) == 0 ? g : NULL; }
typedef struct { P obj, stat; } Ref;  // a value, or (stat) the static type whose members are addressed
static P Member(Ref r, const char *name, int quiet) {
    P type = r.stat ? r.stat : K(r.obj);
    for (P t = type; t; t = il2cpp_class_get_parent(t)) {
        P f = il2cpp_class_get_field_from_name(t, name);
        if (f) { Guard(t, name, "reading"); return fget(f, r.obj); }
        P p = il2cpp_class_get_property_from_name(t, name), g = p ? getter(p) : NULL;
        if (g) { Guard(t, name, "reading"); return pget(g, r.obj); }
    }
    if (quiet) return Missing;
    fail("no field/property '%s' on %s - use type_members to list them", name, Full(type));
}
static P Index(P obj, const char *key) {
    It it;
    if (is(obj, CLS("System.Collections", "IDictionary")) && it_open(&it, obj)) {
        while (it_next(&it)) if (!strcmp(isString(it.key) ? cs(it.key) : tostr(it.key), key)) return it.cur;
        fail("key '%s' not found in dictionary", key);
    }
    char *e; long i = strtol(key, &e, 10);
    if (!*key || *e) fail("index '%s' is not a number", key);
    if (ktype(K(obj)) == 0x1d || is(obj, CLS("System.Collections", "IList"))) {
        int n = count(obj);
        if (i < 0 || i >= n) fail("index %ld out of range (count %d)", i, n);
        it_open(&it, obj); it.i = (int)i - 1; it_next(&it); return it.cur;
    }
    if (!it_open(&it, obj)) fail("%s is not indexable", kname(K(obj)));
    int n = 0;
    while (it_next(&it)) if (n++ == i) return it.cur;
    fail("index %ld out of range (count %d)", i, n);
}
static char *Unquote(const char *tok) { char *k = Sn(tok + 1, strlen(tok) - 2); while (*k == '"' || *k == '\'') k++; for (int n = (int)strlen(k); n && (k[n - 1] == '"' || k[n - 1] == '\'');) k[--n] = 0; return k; }
static Strs Tokens(const char *s) {
    Strs r = {A((strlen(s) + 1) * sizeof(char *)), 0};
    for (int i = 0, n = (int)strlen(s); i < n;) {
        if (s[i] == '.') { i++; continue; }
        if (s[i] == '[') {
            const char *j = strchr(s + i, ']');
            if (!j) fail("missing ']' in path");
            r.v[r.n++] = Sn(s + i, j - s - i + 1); i = (int)(j - s) + 1; continue;
        }
        int k = i; while (k < n && s[k] != '.' && s[k] != '[') k++;
        r.v[r.n++] = Sn(s + i, k - i); i = k;
    }
    return r;
}
static P FindType(const char *name);
static P uiGet(int id);
static Ref Root(const char *path, const char **rest) {
    *rest = "";
    if (starts(path, "#u") || starts(path, "#o")) {
        int k = 2; while (path[k] && (isdigit((unsigned char)path[k]) || (k == 2 && path[k] == '-'))) k++;
        char *id = Sn(path, k);
        P o = ById(id);
        *rest = path + k;
        if (**rest != ':' || !is(o, CLS("UnityEngine", "GameObject"))) return (Ref){o};
        size_t end = strcspn(*rest, ".[");
        P c = FindComponent(o, Sn(*rest + 1, end - 1), id);
        *rest += end;
        return (Ref){c};
    }
    if (*path == '#') {
        int k = 1; while (isdigit((unsigned char)path[k])) k++;
        P el = uiGet(atoi(path + 1));
        if (!el) fail("UI element %s not found (stale id?)", Sn(path, k));
        *rest = path + k;
        return (Ref){el};
    }
    if (*path == '@') {
        const char *s = path + 1;
        size_t head = strcspn(s, "[");
        for (size_t n = head; n > 0; n--) {
            if (n < head && s[n] != '.') continue;
            P t = FindType(Sn(s, n));
            if (!t) continue;
            *rest = s + n;
            return (Ref){NULL, t};
        }
        fail("no type found at the start of '%s' - use types_find", path);
    }
    if (*path == '/') {
        const char *colon = strchr(path, ':');
        char *goPath = colon ? Sn(path, colon - path) : (char *)path;
        P go = FindGo(goPath);
        if (!go) fail("GameObject '%s' not found - use scene_tree", goPath);
        if (!colon) return (Ref){go};
        size_t end = strcspn(colon + 1, ".[");
        *rest = colon + 1 + end;
        return (Ref){FindComponent(go, Sn(colon + 1, end), goPath)};
    }
    fail("path must start with '/' (GameObject path), '@' (static type), '#u'/'#o' (object id from state_get) or '#' (UI element id). Example: /App/Services:OrderService.orders[0].status");
}
static Ref Walk(const char *path, int dropLast, char **last) {
    const char *rest;
    Ref cur = Root(path, &rest);
    Strs toks = Tokens(rest);
    *last = NULL;
    if (dropLast) {
        if (!toks.n) fail("path must end with a member or index");
        *last = toks.v[--toks.n];
    }
    for (int i = 0; i < toks.n; i++) {
        char *tok = toks.v[i];
        if (!cur.stat && !cur.obj) fail("null reached before '%s' in %s", tok, path);
        cur = (Ref){*tok == '[' ? Index(cur.obj, Unquote(tok)) : Member(cur, tok, 0)};
    }
    return cur;
}
static P Resolve(const char *path) { char *l; Ref r = Walk(path, 0, &l); return r.stat ? typeobj(r.stat) : r.obj; }
static P Pick(P x, Strs toks) {
    for (int i = 0; i < toks.n; i++) {
        if (!x || x == Missing) return x;
        x = *toks.v[i] == '[' ? Index(x, Unquote(toks.v[i])) : Member((Ref){x}, toks.v[i], 1);
    }
    return x;
}

// ---------------- formatting ----------------

static char *Short(P v) {
    if (!v) return "null";
    if (isString(v)) return F("\"%s\"", OneLine(cs(v), 120));
    if (Simple(K(v))) return tostr(v);
    if (isU(v)) return alive(v) ? F("%s \"%s\"", kname(K(v)), uname(v)) : F("destroyed %s", kname(K(v)));
    if (isColl(v)) return F("%s(count %d)", TypeName(K(v)), count(v));
    return TypeName(K(v));
}
static char *Brief(P v) { char *id = !v || Simple(K(v)) ? NULL : IdOf(v); return id ? F("%s %s", Short(v), id) : Short(v); }
typedef struct { int limit, props; const char *members; } Opts;
typedef struct { int offset, limit, total, shown; const char *filter; } Page;
typedef struct { char *label, *error; P val; } Entry;
typedef struct { Entry *v; int n, cap; } Entries;
static void epush(Entries *e, char *label, char *error, P val) {
    if (e->n == e->cap) { int c = e->cap ? e->cap * 2 : 32; Entry *q = A(c * sizeof(Entry)); if (e->n) memcpy(q, e->v, e->n * sizeof(Entry)); e->v = q; e->cap = c; }
    e->v[e->n++] = (Entry){label, error, val};
}
static int stopClass(P c) {
    return isName(c, "System", "Object") || isName(c, "UnityEngine", "MonoBehaviour") || isName(c, "UnityEngine", "Behaviour") || isName(c, "UnityEngine", "Component") || isName(c, "UnityEngine", "Object");
}
static int hasAttr(P f, const char *n) { P a = CLS("UnityEngine", n); return a && il2cpp_field_has_attribute(f, a); }
// Collects up to max entries (all of them when max < 0).
static Entries GetEntries(P v, Opts *o, Strs *fields, int max) {
    Entries r = {0};
    It it;
    if (is(v, CLS("System.Collections", "IDictionary")) && it_open(&it, v)) {
        while ((max < 0 || r.n < max) && it_next(&it)) epush(&r, F("[%s]", Short(it.key)), NULL, it.cur);
        return r;
    }
    if (!asObj(v) && it_open(&it, v)) {
        while ((max < 0 || r.n < max) && it_next(&it)) epush(&r, F("[%d]", it.i), NULL, it.cur);
        return r;
    }
    if (fields && fields->v) {
        for (int i = 0; i < fields->n; i++) {
            P val = NULL; char *f = fields->v[i];
            if (TRY(val = Pick(v, Tokens(f)))) epush(&r, f, err, NULL);
            else epush(&r, f, val == Missing ? F("no field/property '%s'", f) : NULL, val == Missing ? NULL : val);
        }
        return r;
    }
    for (P c = K(v); c && !stopClass(c); c = il2cpp_class_get_parent(c)) {
        void *iter = NULL; P f;
        while ((f = il2cpp_class_get_fields(c, &iter))) {
            const char *n = il2cpp_field_get_name(f); int fl = il2cpp_field_get_flags(f);
            if ((fl & 0x10) || (strchr(n, '<') && !ends(n, "k__BackingField"))) continue;
            if (!strcmp(o->members, "public") && (fl & 7) != 6) continue;
            if (!strcmp(o->members, "serialized") && ((fl & 0xa0) || ((fl & 7) != 6 && !hasAttr(f, "SerializeField") && !hasAttr(f, "SerializeReference")))) continue;
            char *label = *n == '<' ? Sn(n + 1, strchr(n, '>') - n - 1) : (char *)n;
            P val = NULL;
            if (TRY(val = fget(f, v))) epush(&r, label, err, NULL); else epush(&r, label, NULL, val);
        }
        if (!o->props) continue;
        iter = NULL; P p;
        while ((p = il2cpp_class_get_properties(c, &iter))) {
            P g = getter(p); uint32_t ifl;
            if (!g || mstatic(g) || (il2cpp_method_get_flags(g, &ifl) & 7) != 6) continue;
            char *label = F("%s (prop)", il2cpp_property_get_name(p));
            P val = NULL;
            if (TRY(val = pget(g, v))) epush(&r, label, err, NULL); else epush(&r, label, NULL, val);
        }
    }
    return r;
}
static int Header(Buf *sb, P v, int ids) {
    if (!v) { bs(sb, "null"); return 1; }
    P t = K(v);
    if (is(v, CLS("System", "Type"))) { bf(sb, "type %s", Full(il2cpp_class_from_system_type(v))); return 1; }
    if (isString(v) || Simple(t)) { bs(sb, Short(v)); return 1; }
    if (isU(v) && !alive(v)) { bf(sb, "destroyed %s", kname(t)); return 1; }
    int obj = asObj(v);
    bs(sb, isU(v) ? F("%s \"%s\"", kname(t), uname(v)) : TypeName(t));
    if (!obj && isColl(v)) bf(sb, " count=%d", count(v));
    char *id = ids ? IdOf(v) : NULL;
    if (id) bf(sb, " %s", id);
    if (obj && Why(t, "")) { bs(sb, " (contents hidden by the safety policy)"); return 1; }
    return 0;
}
static char *Line(P x, int ids) {
    Buf sb = {0};
    if (Header(&sb, x, ids) || !asObj(x)) return bstr(&sb);
    Opts o = {30, 0, "all"};
    Entries es = GetEntries(x, &o, NULL, 9);
    bs(&sb, " {");
    for (int n = 0; n < es.n; n++) {
        if (n >= 8) { bs(&sb, ", ..."); break; }
        bf(&sb, "%s%s=%s", n ? ", " : "", es.v[n].label, es.v[n].error ? "<error>" : Short(es.v[n].val));
    }
    bs(&sb, "}");
    return OneLine(bstr(&sb), 400);
}
static void Format(Buf *sb, P v, Opts *o, int depth, Strs *fields, int indent, Page *page) {
    if (sb->n > 20000 || indent > 14) { bs(sb, "..."); return; }
    if (Header(sb, v, 1) || depth <= 0) return;
    int obj = asObj(v), from = page ? page->offset : 0, limit = page ? page->limit : obj ? 80 : o->limit, n = 0, shown = 0, more = 0;
    char *pad = F("%*s", (indent + 1) * 2, "");
    Entries es = GetEntries(v, o, obj ? NULL : fields, page ? -1 : limit + 1);
    if (obj && fields) es = GetEntries(v, o, fields, -1);
    for (int i = 0; i < es.n; i++) {
        Entry *e = &es.v[i];
        if (page && page->filter && !icontains(e->label, page->filter) && (e->error || !icontains(Line(e->val, 0), page->filter))) continue;
        if (n++ < from) continue;
        if (shown >= limit) { more = 1; if (!page) break; continue; }
        shown++;
        bf(sb, "\n%s%s = ", pad, e->label);
        if (e->error) bf(sb, "<error: %s>", e->error);
        else Format(sb, e->val, o, obj ? depth - 1 : depth, obj ? NULL : fields, indent + 1, NULL);
    }
    if (page) { page->total = n; page->shown = shown; }
    else if (more) bf(sb, "\n%s%s", pad, isColl(v) ? F("... %d more", count(v) - limit) : "... more");
}
static char *Fmt(P v, int depth, int max, int props) { Buf b = {0}; Opts o = {max, props, "all"}; Format(&b, v, &o, depth, NULL, 0, NULL); return bstr(&b); }

// ---------------- JSON -> managed values ----------------

static int unsafeType(P k) {
    int t = ktype(k);
    return t == 0x18 || t == 0x19 || t == 0xf || isk(k, CLS("System.Reflection", "MemberInfo")) || isk(k, CLS("System", "Delegate")) || !strcmp(nsOf(k), "System.Reflection");
}
static int isInt(cJSON *j) { return cJSON_IsNumber(j) && j->valuedouble == floor(j->valuedouble) && fabs(j->valuedouble) < 9.2e18; }
static char *enumNames(P k) {
    Buf b = {0}; void *it = NULL; P f;
    while ((f = il2cpp_class_get_fields(k, &it))) if (il2cpp_field_get_flags(f) & 0x40) bf(&b, "%s%s", b.n ? ", " : "", il2cpp_field_get_name(f));
    return bstr(&b);
}
static void setField(P obj, P f, P val) {
    P fk = tk(il2cpp_field_get_type(f));
    void *p = isvt(fk) ? (val ? il2cpp_object_unbox(val) : A(il2cpp_class_value_size(fk, NULL) + 16)) : (void *)&val;
    if (il2cpp_field_get_flags(f) & 0x10) { il2cpp_runtime_class_init(il2cpp_field_get_parent(f)); il2cpp_field_static_set_value(f, isvt(fk) ? p : val); }
    else il2cpp_field_set_value(obj, f, p);
}
static void *argp(P type, P val) {  // a runtime_invoke argument slot
    P k = tk(type);
    if (il2cpp_type_is_byref(type)) fail("ref/out parameters are not supported");
    return isvt(k) ? (val ? il2cpp_object_unbox(val) : A(il2cpp_class_value_size(k, NULL) + 16)) : val;
}
static P To(cJSON *j, P type) {
    P k = tk(type);
    int t = ttype(type);
    if (isName(k, "System", "Nullable`1")) fail("Nullable parameters are not supported by the native agent");
    if (!j || cJSON_IsNull(j)) return isvt(k) ? il2cpp_object_new(k) : NULL;
    if (il2cpp_class_is_enum(k)) {
        P ek = tk(il2cpp_class_enum_basetype(k));
        int64_t v = 0;
        if (isInt(j)) v = (int64_t)j->valuedouble;
        else {
            char *s = jtext(j); void *it = NULL; P f; int found = 0;
            while (!found && (f = il2cpp_class_get_fields(k, &it)))
                if ((il2cpp_field_get_flags(f) & 0x40) && ieq(il2cpp_field_get_name(f), s)) { P b = fget(f, NULL); memcpy(&v, il2cpp_object_unbox(b), il2cpp_class_value_size(ek, NULL)); found = 1; }
            if (!found) fail("'%s' is not a value of enum %s. Valid: %s", s, kname(k), enumNames(k));
        }
        P o = il2cpp_object_new(k); memcpy(il2cpp_object_unbox(o), &v, il2cpp_class_value_size(ek, NULL)); return o;
    }
    if (!allowUnsafe && unsafeType(k))
        fail("a value of type %s cannot be passed in under the safety policy. Only a human can lift this, by starting the game/editor with environment variable UDRIVER_UNSAFE=1.", kname(k));
    if (t >= 2 && t <= 0xd) {
        double d; char *e;
        if (cJSON_IsNumber(j)) d = j->valuedouble;
        else if (cJSON_IsBool(j)) d = cJSON_IsTrue(j);
        else if (t == 2 && cJSON_IsString(j) && (ieq(j->valuestring, "true") || ieq(j->valuestring, "false"))) d = ieq(j->valuestring, "true");
        else if (t == 3 && cJSON_IsString(j) && strlen(j->valuestring) == 1) d = (unsigned char)*j->valuestring;
        else if (cJSON_IsString(j) && (d = strtod(j->valuestring, &e), e != j->valuestring && !*e)) {}
        else fail("could not convert %s to %s", Cut(jtext(j), 80), TypeName(k));
        union { bool b; uint16_t c; int8_t i1; uint8_t u1; int16_t i2; uint16_t u2; int32_t i4; uint32_t u4; int64_t i8; uint64_t u8; float r4; double r8; } u = {0};
        switch (t) {
        case 2: u.b = d != 0; break; case 3: u.c = (uint16_t)d; break; case 4: u.i1 = (int8_t)d; break; case 5: u.u1 = (uint8_t)d; break;
        case 6: u.i2 = (int16_t)d; break; case 7: u.u2 = (uint16_t)d; break; case 8: u.i4 = (int32_t)d; break; case 9: u.u4 = (uint32_t)d; break;
        case 0xa: u.i8 = (int64_t)d; break; case 0xb: u.u8 = (uint64_t)d; break; case 0xc: u.r4 = (float)d; break; default: u.r8 = d;
        }
        return box(k, &u);
    }
    if (t == 0xe) return ms(jtext(j));
    if (t == 0x1c) {
        if (isInt(j)) { int64_t v = (int64_t)j->valuedouble; return box(CLS("System", "Int64"), &v); }
        if (cJSON_IsNumber(j)) return box(CLS("System", "Double"), &j->valuedouble);
        if (cJSON_IsBool(j)) { bool b = cJSON_IsTrue(j); return box(CLS("System", "Boolean"), &b); }
        if (cJSON_IsString(j)) return ms(j->valuestring);
        fail("pass a number, string or bool for a parameter of type object");
    }
    if (t == 0x1d && cJSON_IsArray(j)) {
        P ek = il2cpp_class_get_element_class(k), arr = il2cpp_array_new(ek, cJSON_GetArraySize(j)), et = il2cpp_class_get_type(ek);
        int i = 0, es = il2cpp_array_element_size(K(arr)); cJSON *x;
        cJSON_ArrayForEach(x, j) {
            P v = To(x, et);
            if (isvt(ek)) memcpy(ARR(arr) + (size_t)i * es, il2cpp_object_unbox(v), es); else il2cpp_gc_wbarrier_set_field(arr, (void **)ARR(arr) + i, v);
            i++;
        }
        return arr;
    }
    if (cJSON_IsArray(j) && !isvt(k) && mth(k, "Add", 1) && mth(k, ".ctor", 0)) {
        P o = il2cpp_object_new(k), add = mth(k, "Add", 1), pt = il2cpp_method_get_param(add, 0); cJSON *x;
        inv(mth(k, ".ctor", 0), o, NULL);
        cJSON_ArrayForEach(x, j) { void *a[] = {argp(pt, To(x, pt))}; inv(add, o, a); }
        return o;
    }
    if (cJSON_IsObject(j)) {
        P o = il2cpp_object_new(k), ctor = isvt(k) ? NULL : mth(k, ".ctor", 0);
        if (!isvt(k)) { if (!ctor) fail("%s has no parameterless constructor", TypeName(k)); inv(ctor, o, NULL); }
        cJSON *x;
        cJSON_ArrayForEach(x, j) {
            P f = NULL, s = NULL;
            for (P c = k; c && !f && !s; c = il2cpp_class_get_parent(c)) {
                void *it = NULL; P q;
                while (!f && (q = il2cpp_class_get_fields(c, &it))) if (!(il2cpp_field_get_flags(q) & 0x10) && ieq(il2cpp_field_get_name(q), x->string)) f = q;
                it = NULL;
                while (!f && !s && (q = il2cpp_class_get_properties(c, &it))) if (ieq(il2cpp_property_get_name(q), x->string) && il2cpp_property_get_set_method(q)) s = il2cpp_property_get_set_method(q);
            }
            if (f) setField(o, f, To(x, il2cpp_field_get_type(f)));
            else if (s) { P pt = il2cpp_method_get_param(s, 0); void *a[] = {argp(pt, To(x, pt))}; inv(s, o, a); }
        }
        return o;
    }
    fail("could not convert %s to %s", Cut(jtext(j), 80), TypeName(k));
}

// ---------------- types ----------------

static int asmIsEngine(const char *n) { return anyStarts(n, EnginePrefixes); }
static Vec images(int engine) {  // engine: 0 game only, 1 engine only, 2 all
    Vec r = {0}; size_t c; P *as = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &c);
    for (size_t i = 0; i < c; i++) { P im = il2cpp_assembly_get_image(as[i]); if (engine == 2 || asmIsEngine(il2cpp_image_get_name(im)) == engine) vpush(&r, im); }
    return r;
}
static struct { char *name; P k; } typeCache[256];
static P FindType(const char *name) {
    for (int i = 0; i < 256 && typeCache[i].name; i++) if (!strcmp(typeCache[i].name, name)) return typeCache[i].k;
    P found = NULL;
    const char *dot = strrchr(name, '.');
    char *ns = dot ? Sn(name, dot - name) : "", *n = dot ? (char *)dot + 1 : (char *)name;
    for (int pass = 0; pass < 2 && !found; pass++) {
        Vec im = images(pass);
        P byShort = NULL;
        for (int i = 0; i < im.n && !found; i++) {
            found = il2cpp_class_from_name(im.v[i], ns, n);
            for (size_t j = 0, c = dot || byShort ? 0 : il2cpp_image_get_class_count(im.v[i]); j < c && !byShort; j++) {
                P k = il2cpp_image_get_class(im.v[i], j);
                if (!strcmp(kname(k), name)) byShort = k;
            }
        }
        if (!found) found = byShort;
    }
    for (int i = 0; i < 256 && found; i++) if (!typeCache[i].name) { typeCache[i].name = strdup(name); typeCache[i].k = found; break; }
    return found;
}
static char *Sig(P m) {
    Buf b = {0};
    bf(&b, "%s%s %s(", mstatic(m) ? "static " : "", tn(il2cpp_method_get_return_type(m)), il2cpp_method_get_name(m));
    for (uint32_t i = 0, n = il2cpp_method_get_param_count(m); i < n; i++) bf(&b, "%s%s %s", i ? ", " : "", tn(il2cpp_method_get_param(m, i)), il2cpp_method_get_param_name(m, i));
    bs(&b, ")");
    return bstr(&b);
}
static char *asmName(P k) { const char *a = il2cpp_class_get_assemblyname(k); return ends(a, ".dll") ? Sn(a, strlen(a) - 4) : (char *)a; }

// ---------------- state tools ----------------

static int t_state_get(void) {
    char *path = astr("path", NULL);
    if (!path) fail("path is required");
    int lim = aint("maxItems", 30);
    Opts o = {lim = aint("limit", lim) < 1 ? 1 : aint("limit", lim), abool("props", 0), astr("members", "all")};
    if (strcmp(o.members, "all") && strcmp(o.members, "public") && strcmp(o.members, "serialized")) fail("members must be all, public or serialized");
    int off = aint("offset", 0);
    Page page = {off < 0 ? 0 : off, o.limit, -1, 0, astr("filter", NULL)};
    Buf sb = {0}; bf(&sb, "%s = ", path);
    int depth = aint("depth", 1); depth = depth < 0 ? 0 : depth > 4 ? 4 : depth;
    P v = Resolve(path);
    Strs fields = astrs("fields");
    Format(&sb, v, &o, depth == 0 && fields.v ? 1 : depth, fields.v ? &fields : NULL, 0, &page);
    int cut = sb.n > 20000;
    if (cut) sb.p[20000] = 0;
    Text("%s%s", bstr(&sb), cut ? "\n..." : "");
    if (page.total < 0) { Text("shown 1-1 of 1"); return 0; }
    Text("%s%s%s%s", page.shown == 0 ? F("shown 0 of %d", page.total) : F("shown %d-%d of %d", page.offset + 1, page.offset + page.shown, page.total),
         page.filter ? F(" matching '%s'", page.filter) : "", page.offset + page.shown < page.total ? F("; next page: offset=%d", page.offset + page.shown) : "",
         cut ? "; output cut at 20000 characters - use fields, limit or a lower depth" : "");
    return 0;
}
static P itemSetter(P obj, P *keyType, P *valType) {
    P s = mth(K(obj), "set_Item", 2);
    if (!s) fail("target is not a list or dictionary");
    s = vmeth(obj, s);
    Guard(il2cpp_method_get_class(s), "set_Item", "state_set");
    *keyType = il2cpp_method_get_param(s, 0); *valType = il2cpp_method_get_param(s, 1);
    return s;
}
static int t_state_set(void) {
    char *path = astr("path", NULL), *last;
    cJSON *val = cJSON_GetObjectItemCaseSensitive(J->args, "value");
    if (!path || !val) fail("path and value are required");
    Ref parent = Walk(path, 1, &last);
    if (*last == '[') {
        char *key = Unquote(last);
        P o = parent.obj;
        if (o && ktype(K(o)) == 0x1d) {
            int i = atoi(key), n = ALEN(o);
            if (i < 0 || i >= n) fail("index %d out of range (count %d)", i, n);
            P ek = il2cpp_class_get_element_class(K(o)), v = To(val, il2cpp_class_get_type(ek));
            int es = il2cpp_array_element_size(K(o));
            if (isvt(ek)) memcpy(ARR(o) + (size_t)i * es, il2cpp_object_unbox(v), es); else il2cpp_gc_wbarrier_set_field(o, (void **)ARR(o) + i, v);
        } else if (o && (is(o, CLS("System.Collections", "IList")) || is(o, CLS("System.Collections", "IDictionary")))) {
            P kt, vt, s = itemSetter(o, &kt, &vt);
            int list = is(o, CLS("System.Collections", "IList"));
            if (list) { int i = atoi(key), n = count(o); if (i < 0 || i >= n) fail("index %d out of range (count %d)", i, n); }
            cJSON *kj = cJSON_CreateString(key);
            P kv = NULL; volatile int bad = TRY(kv = To(kj, kt));
            cJSON_Delete(kj);
            if (bad) fail("%s", err);
            void *a[] = {argp(kt, kv), argp(vt, To(val, vt))};
            inv(s, o, a);
        } else fail("target is not a list or dictionary");
    } else {
        P type = parent.stat ? parent.stat : parent.obj ? K(parent.obj) : NULL;
        if (!type) fail("null reached before '%s' in %s", last, path);
        int done = 0;
        for (P t = type; t && !done; t = il2cpp_class_get_parent(t)) {
            P f = il2cpp_class_get_field_from_name(t, last);
            if (f) { Guard(t, last, "state_set"); setField(parent.obj, f, To(val, il2cpp_field_get_type(f))); done = 1; break; }
            P p = il2cpp_class_get_property_from_name(t, last), s = p ? il2cpp_property_get_set_method(p) : NULL;
            if (s) {
                Guard(t, last, "state_set");
                P pt = il2cpp_method_get_param(s, 0); void *a[] = {argp(pt, To(val, pt))};
                if (mstatic(s)) inv(s, NULL, a); else inv(vmeth(parent.obj, s), parent.obj, a);
                done = 1;
            }
        }
        if (!done) fail("no writable field/property '%s' on %s", last, Full(type));
    }
    Text("%s = %s", path, Fmt(Resolve(path), 1, 30, 0));
    return 0;
}
static int TypesMatch(P m, Strs types) {
    for (int i = 0; i < types.n; i++) {
        P t = il2cpp_method_get_param(m, i), k = tk(t);
        if (!ieq(types.v[i], tn(t)) && !ieq(types.v[i], kname(k)) && strcmp(types.v[i], Full(k))) return 0;
    }
    return 1;
}
static int Fit(cJSON *a, P type) {
    P u = tk(type); int t = ttype(type), vt = isvt(u);
    int number = (t >= 4 && t <= 0xd) || isName(u, "System", "Decimal"), en = il2cpp_class_is_enum(u), obj = t == 0x1c;
    if (cJSON_IsNull(a)) return !vt ? 2 : -1;
    if (isInt(a)) return t == 8 ? 4 : t == 0xa ? 3 : number || en ? 2 : obj ? 1 : -1;
    if (cJSON_IsNumber(a)) return t == 0xd ? 4 : t == 0xc ? 3 : isName(u, "System", "Decimal") ? 2 : obj ? 1 : -1;
    if (cJSON_IsBool(a)) return t == 2 ? 4 : obj ? 1 : -1;
    if (cJSON_IsString(a)) return t == 0xe ? 4 : en || t == 3 ? 2 : obj ? 1 : number || t == 2 ? -1 : 0;
    if (cJSON_IsArray(a)) return t == 0x1d || isk(u, CLS("System.Collections", "IList")) ? 3 : obj ? 1 : 0;
    return prim(u) || t == 0xe ? -1 : 0;
}
typedef struct { int score; P m; } FitM;
static int byScore(const void *a, const void *b) { return ((FitM *)b)->score - ((FitM *)a)->score; }
static int t_state_call(void) {
    char *path = astr("path", NULL), *method = astr("method", NULL), *last;
    if (!path || !method) fail("path and method are required");
    Ref target = Walk(path, 0, &last);
    if (!target.stat && !target.obj) fail("target is null");
    if (target.stat) Guard(target.stat, method, "state_call");
    cJSON *args = arg("args"); int argc = cJSON_IsArray(args) ? cJSON_GetArraySize(args) : 0;
    Strs types = astrs("types");
    P type = target.stat ? target.stat : K(target.obj);
    Strs seen = {A(512 * sizeof(char *)), 0};
    FitM fits[256]; int nf = 0;
    for (P t = type; t; t = il2cpp_class_get_parent(t)) {
        void *it = NULL; P m;
        while ((m = il2cpp_class_get_methods(t, &it))) {
            if (strcmp(il2cpp_method_get_name(m), method)) continue;
            char *sig = Sig(m); int dup = 0;
            for (int i = 0; i < seen.n && !dup; i++) dup = !strcmp(seen.v[i], sig);
            if (dup || seen.n >= 512) continue;
            seen.v[seen.n++] = sig;
            if ((int)il2cpp_method_get_param_count(m) != argc || (target.stat && !mstatic(m)) || il2cpp_method_is_generic(m)) continue;
            if (types.v && (types.n != argc || !TypesMatch(m, types))) continue;
            int score = 0;
            for (int i = 0; i < argc && score >= 0; i++) { int f = Fit(cJSON_GetArrayItem(args, i), il2cpp_method_get_param(m, i)); score = f < 0 ? -1 : score + f; }
            if (score >= 0 && nf < 256) fits[nf++] = (FitM){score, m};
        }
    }
    if (!seen.n) fail("no method '%s' on %s", method, Full(type));
    qsort(fits, nf, sizeof *fits, byScore);
    if (nf > 1 && fits[0].score == fits[1].score) {
        Strs tied = {A(nf * sizeof(char *)), 0};
        for (int i = 0; i < nf; i++) if (fits[i].score == fits[0].score) tied.v[tied.n++] = Sig(fits[i].m);
        fail("the arguments fit several overloads of '%s' equally: %s. Pick one with types, e.g. types=[\"string\"].", method, join(tied, "; "));
    }
    char *refused = NULL;
    for (int i = 0; i < nf; i++) {
        P m = fits[i].m;
        Guard(il2cpp_method_get_class(m), method, "state_call");
        void **av = A((argc + 1) * sizeof(void *));
        if (TRY(for (int a = 0; a < argc; a++) { P pt = il2cpp_method_get_param(m, a); av[a] = argp(pt, To(cJSON_GetArrayItem(args, a), pt)); })) { refused = err; continue; }
        P res = mstatic(m) ? inv(m, NULL, av) : inv(vmeth(target.obj, m), target.obj, av);
        Text("called %s -> %s", Sig(m), ttype(il2cpp_method_get_return_type(m)) == 1 ? "void" : Fmt(res, 1, 30, 0));
        return 0;
    }
    fail("no overload of '%s' fits these %d argument(s)%s. Overloads: %s%s", method, argc, types.v ? " and types" : "", join(seen, "; "), refused ? F(". %s", refused) : "");
}

// ---------------- state_query ----------------

typedef struct { int kind, isStr; double d; char *s; } Val;  // kind 0 null, 1 number, 2 text
static int Number(P v, double *d) {
    if (!v || v == Missing) return 0;
    P k = K(v); int t = ktype(k);
    if (il2cpp_class_is_enum(k) || t < 4 || t > 0xd) {
        if (!isName(k, "System", "Decimal")) return 0;
        *d = strtod(tostr(v), NULL); return 1;
    }
    void *p = il2cpp_object_unbox(v);
    switch (t) {
    case 4: *d = *(int8_t *)p; break; case 5: *d = *(uint8_t *)p; break; case 6: *d = *(int16_t *)p; break; case 7: *d = *(uint16_t *)p; break;
    case 8: *d = *(int32_t *)p; break; case 9: *d = *(uint32_t *)p; break; case 0xa: *d = (double)*(int64_t *)p; break; case 0xb: *d = (double)*(uint64_t *)p; break;
    case 0xc: *d = *(float *)p; break; default: *d = *(double *)p;
    }
    return 1;
}
static char *TextOf(P v) { return !v ? "null" : isString(v) ? cs(v) : tostr(v); }
static Val vOf(P v) {
    Val r = {0};
    if (!v || v == Missing || (isU(v) && !alive(v))) return r;
    if (Number(v, &r.d)) { r.kind = 1; return r; }
    r.kind = 2; r.s = TextOf(v); r.isStr = isString(v);
    return r;
}
static Val vOfJ(cJSON *j) {
    Val r = {0};
    if (!j || cJSON_IsNull(j)) return r;
    if (cJSON_IsNumber(j)) { r.kind = 1; r.d = j->valuedouble; return r; }
    r.kind = 2; r.s = jtext(j); r.isStr = cJSON_IsString(j);
    return r;
}
static int Compare(Val x, Val y) {
    if (!x.kind && !y.kind) return 0;
    if (!x.kind) return -1;
    if (!y.kind) return 1;
    char *e;
    if (x.kind == 1 && y.kind == 2 && y.isStr) { double d = strtod(y.s, &e); if (e != y.s && !*e) { y.kind = 1; y.d = d; } }
    if (y.kind == 1 && x.kind == 2 && x.isStr) { double d = strtod(x.s, &e); if (e != x.s && !*e) { x.kind = 1; x.d = d; } }
    if (x.kind == 1 && y.kind == 1) return x.d < y.d ? -1 : x.d > y.d;
    char *a = x.kind == 1 ? fstr(x.d) : x.s, *b = y.kind == 1 ? fstr(y.d) : y.s;
    int c = strcasecmp(a, b); return c < 0 ? -1 : c > 0;
}
static const char *Ops[] = {"==", "!=", "<", "<=", ">", ">=", "contains", 0};
static int Test(P v, const char *op, cJSON *want) {
    if (v == Missing) return 0;
    Val x = vOf(v), w = vOfJ(want);
    if (!strcmp(op, "contains")) {
        if (!x.kind || !w.kind) return 0;
        It it;
        if (!isString(v) && it_open(&it, v)) { while (it_next(&it)) if (!Compare(vOf(it.cur), w)) return 1; return 0; }
        return icontains(x.kind == 1 ? fstr(x.d) : x.s, w.kind == 1 ? fstr(w.d) : w.s);
    }
    if (!x.kind || !w.kind) return !strcmp(op, "==") ? x.kind == w.kind : !strcmp(op, "!=") && x.kind != w.kind;
    int c = Compare(x, w);
    if (!strcmp(op, "==")) return c == 0;
    if (!strcmp(op, "!=")) return c != 0;
    if (!strcmp(op, "<")) return c < 0;
    if (!strcmp(op, "<=")) return c <= 0;
    if (!strcmp(op, ">")) return c > 0;
    if (!strcmp(op, ">=")) return c >= 0;
    fail("op must be one of == != < <= > >= contains");
}
typedef struct { char *field, *op; Strs toks; cJSON *value; int missed; } Cond;
static Cond ParseCond(cJSON *t) {
    cJSON *f = cJSON_GetObjectItemCaseSensitive(t, "field"), *op = cJSON_GetObjectItemCaseSensitive(t, "op");
    if (!cJSON_IsObject(t) || !f || cJSON_IsNull(f)) fail("each condition is {\"field\":\"hp\",\"op\":\"<\",\"value\":10} (field \".\" is the item itself)");
    Cond c = {jtext(f), op && !cJSON_IsNull(op) ? jtext(op) : "==", {0}, cJSON_GetObjectItemCaseSensitive(t, "value")};
    int ok = 0; for (const char **o = Ops; *o; o++) ok |= !strcmp(*o, c.op);
    if (!ok) fail("op '%s' is not supported; use one of == != < <= > >= contains", c.op);
    c.toks = Tokens(c.field);
    return c;
}
typedef struct { int count, numbers; double min, max, sum; Map strings; } Stat;
static void statAdd(Stat *s, P v) {
    if (v == Missing) return;
    s->count++;
    double d;
    if (Number(v, &d)) { s->numbers++; s->sum += d; if (d < s->min) s->min = d; if (d > s->max) s->max = d; }
    else if (v && (isString(v) || il2cpp_class_is_enum(K(v)) || ktype(K(v)) == 2 || ktype(K(v)) == 3)) mget(&s->strings, TextOf(v));
}
static char *g3(double d) {  // "0.###"
    char *s = F("%.3f", d); int n = (int)strlen(s);
    while (s[n - 1] == '0') s[--n] = 0;
    if (s[n - 1] == '.') s[--n] = 0;
    return !strcmp(s, "-0") ? "0" : s;
}
static char *Describe(Stat *s) {
    char *r = F("%d value(s)", s->count);
    if (s->numbers) r = F("%s; numbers %d: min %s, max %s, avg %s", r, s->numbers, g3(s->min), g3(s->max), g3(s->sum / s->numbers));
    if (s->strings.n) r = F("%s; text: %d unique", r, s->strings.n);
    return r;
}
typedef struct { Val key; int i; P x; } Kept;
static int sortDesc;
static int byKey(const void *a, const void *b) { const Kept *p = a, *q = b; int c = sortDesc ? Compare(q->key, p->key) : Compare(p->key, q->key); return c ? c : p->i - q->i; }
static int byCount(const void *a, const void *b) { return ((int *)b)[1] - ((int *)a)[1]; }
static int t_state_query(void) {
    char *path = astr("path", NULL);
    if (!path) fail("path is required, e.g. /Game:Inventory.items");
    P src = Resolve(path);
    It it;
    if (!src || isString(src) || !isEnumerable(src) || is(src, kTransform()) || !it_open(&it, src))
        fail("%s is %s, not a collection - point path at a list, array, set or dictionary", path, !src ? "null" : F("a %s", TypeName(K(src))));
    Cond where[64]; int nw = 0; cJSON *w = arg("where"), *x;
    if (cJSON_IsArray(w)) { cJSON_ArrayForEach(x, w) if (nw < 64) where[nw++] = ParseCond(x); }
    else if (w) where[nw++] = ParseCond(w);
    Strs select = astrs("select"), selToks[64];
    if (select.n > 64) select.n = 64;
    for (int i = 0; i < select.n; i++) selToks[i] = Tokens(select.v[i]);
    char *sort = astr("sort", NULL), *grp = astr("group", NULL);
    sortDesc = sort && *sort == '-';
    Strs sortToks = {0}, groupToks = {0};
    if (sort) { while (*sort == '-' || *sort == '+') sort++; sortToks = Tokens(sort); }
    if (grp) groupToks = Tokens(grp);
    int hasSort = !!astr("sort", NULL), hasGroup = !!grp, stats = abool("stats", 0);
    int limit = aint("limit", 20), offset = aint("offset", 0);
    if (limit < 0) limit = 0;
    if (offset < 0) offset = 0;
    Kept *keep = NULL; int nk = 0, capk = 0, matched = 0, early = 0, scanned = 0;
    Map groups = {0}, typesM = {0};
    Stat stat[64]; for (int i = 0; i < 64; i++) stat[i] = (Stat){0, 0, 1.7976931348623157e308, -1.7976931348623157e308, 0, {0}};
    long long t0 = now_ms();
    while (it_next(&it)) {
        P item = it.cur; int i = it.i, pass = 1;
        scanned++;
        for (int c = 0; c < nw && pass; c++) {
            P v = Pick(item, where[c].toks);
            if (v == Missing) where[c].missed++;
            pass = Test(v, where[c].op, where[c].value);
        }
        if (!pass) continue;
        matched++;
        if (hasSort || (matched > offset && nk < limit)) {
            if (nk == capk) { capk = capk ? capk * 2 : 64; Kept *q = A(capk * sizeof(Kept)); if (nk) memcpy(q, keep, nk * sizeof(Kept)); keep = q; }
            keep[nk++] = (Kept){hasSort ? vOf(Pick(item, sortToks)) : (Val){0}, i, item};
        }
        if (hasGroup) {
            P g = Pick(item, groupToks);
            (*mget(&groups, g == Missing ? "(missing)" : !g || Simple(K(g)) ? TextOf(g) : Brief(g)))++;
        }
        if (stats) {
            (*mget(&typesM, item ? TypeName(K(item)) : "null"))++;
            if (!select.n) statAdd(&stat[0], item);
            else for (int s = 0; s < select.n; s++) statAdd(&stat[s], Pick(item, selToks[s]));
        }
        early = !hasSort && !hasGroup && !stats && nk >= limit;
        if (early) break;
    }
    long long ms = now_ms() - t0;
    if (hasSort) {
        qsort(keep, nk, sizeof *keep, byKey);
        int from = offset < nk ? offset : nk, n = nk - offset < limit ? nk - offset : limit;
        keep += from; nk = n < 0 ? 0 : n;
    }
    Text("%s: %s, %s%lld ms over 1 frame(s)", path, early ? F("stopped after %d item(s) once %d matched", scanned, limit) : F("scanned %d item(s)", scanned),
         early ? "" : F("%d matched, ", matched), ms);
    for (int c = 0; c < nw; c++) if (where[c].missed) Text("note: '%s' does not exist on %d item(s); those never match", where[c].field, where[c].missed);
    for (int k = 0; k < nk; k++) {
        P item = keep[k].x;
        Buf sb = {0}; bf(&sb, "[%d] ", keep[k].i);
        if (!select.v) bs(&sb, Line(item, 1));
        else {
            char *id = !item || Simple(K(item)) ? NULL : IdOf(item);
            if (id) bf(&sb, "%s ", id);
            for (int s = 0; s < select.n; s++) {
                P v = NULL;
                char *txt = TRY(v = Pick(item, selToks[s])) ? F("<error: %s>", err) : v == Missing ? "<none>" : Brief(v);
                bf(&sb, "%s%s=%s", s ? ", " : "", select.v[s], txt);
            }
        }
        Text("%s", OneLine(bstr(&sb), 600));
    }
    if (!early) Text("%s", nk == 0 ? F("shown 0 of %d", matched) : F("shown %d-%d of %d%s", offset + 1, offset + nk, matched, offset + nk < matched ? F("; next page: offset=%d", offset + nk) : ""));
    else Text("shown %d-%d; more may follow: next page offset=%d (add stats=true for the full count)", offset + 1, offset + nk, offset + nk);
    if (hasGroup) {
        int (*gl)[2] = A((groups.n + 1) * sizeof *gl);
        for (int i = 0; i < groups.n; i++) { gl[i][0] = i; gl[i][1] = groups.v[i]; }
        qsort(gl, groups.n, sizeof *gl, byCount);
        Text("group by %s: %d group(s)", grp, groups.n);
        for (int i = 0; i < groups.n && i < 30; i++) Text("  %s: %d", OneLine(groups.k[gl[i][0]], 120), gl[i][1]);
        if (groups.n > 30) Text("  ... %d more group(s)", groups.n - 30);
    }
    if (stats) {
        Buf tl = {0};
        for (int i = 0; i < typesM.n; i++) bf(&tl, "%s%s x%d", i ? ", " : "", typesM.k[i], typesM.v[i]);
        Text("stats: count %d; types: %s", matched, bstr(&tl));
        if (!select.n) Text("  items: %s", Describe(&stat[0]));
        else for (int s = 0; s < select.n; s++) Text("  %s: %s", select.v[s], Describe(&stat[s]));
    }
    return 0;
}

// ---------------- types_find, type_members ----------------

static int t_types_find(void) {
    char *pattern = astr("pattern", NULL);
    if (!pattern) fail("pattern is required");
    int limit = aint("limit", 40), n = 0, more = 0;
    Vec im = images(abool("includeEngine", 0) ? 2 : 0);
    P obj = CLS("System", "Object");
    for (int i = 0; i < im.n; i++)
        for (size_t j = 0, c = il2cpp_image_get_class_count(im.v[i]); j < c; j++) {
            P t = il2cpp_image_get_class(im.v[i], j);
            if (strchr(kname(t), '<') || !(matches(pattern, Full(t)) || matches(pattern, kname(t)))) continue;
            if (n >= limit) { more++; continue; }
            n++;
            int fl = il2cpp_class_get_flags(t), en = il2cpp_class_is_enum(t);
            P base = il2cpp_class_get_parent(t);
            Text("%s  [%s]%s%s", Full(t), asmName(t), (fl & 0x80) && (fl & 0x100) ? " static" : (fl & 0x20) ? " interface" : en ? " enum" : "",
                 base && base != obj && !en ? F(" : %s", TypeName(base)) : "");
        }
    Text("%d type(s)%s", n, more ? F(", %d more not shown - refine the pattern", more) : "");
    return 0;
}
static int t_type_members(void) {
    char *name = astr("type", NULL), *contains = astr("contains", NULL);
    P type = name ? FindType(name) : NULL;
    if (!type) fail("type '%s' not found - use types_find", name ? name : "");
    int inherited = abool("inherited", 0), n = 0;
    P base = il2cpp_class_get_parent(type);
    Text("%s%s", Full(type), base ? F(" : %s", TypeName(base)) : "");
    for (P t = type; t && !isName(t, "System", "Object") && n < 200; t = inherited ? il2cpp_class_get_parent(t) : NULL) {
        if (starts(nsOf(t), "UnityEngine") && t != type) break;
        void *it = NULL; P f, p, m;
        while ((f = il2cpp_class_get_fields(t, &it))) {
            const char *fn = il2cpp_field_get_name(f);
            if (strchr(fn, '<') || (contains && !icontains(fn, contains))) continue;
            Text("  field  %s%s %s", il2cpp_field_get_flags(f) & 0x10 ? "static " : "", tn(il2cpp_field_get_type(f)), fn); n++;
        }
        it = NULL;
        while ((p = il2cpp_class_get_properties(t, &it))) {
            const char *pn = il2cpp_property_get_name(p);
            if (contains && !icontains(pn, contains)) continue;
            Text("  prop   %s %s {%s%s }", tn(pType(p)), pn, il2cpp_property_get_get_method(p) ? " get;" : "", il2cpp_property_get_set_method(p) ? " set;" : ""); n++;
        }
        it = NULL;
        while ((m = il2cpp_class_get_methods(t, &it))) {
            uint32_t ifl; const char *mn = il2cpp_method_get_name(m);
            if ((il2cpp_method_get_flags(m, &ifl) & 0x800) || strchr(mn, '<') || (contains && !icontains(mn, contains))) continue;
            Text("  method %s", Sig(m)); n++;
        }
    }
    Text("%d member(s)%s", n, inherited ? "" : " declared on this type (inherited=true for base types)");
    return 0;
}

// ---------------- scene_tree, status ----------------

static void treeWalk(Buf *sb, P t, int d, int depth, int max, int activeOnly, int *count, int *skipped) {
    P go = goOfC(t);
    if (activeOnly && !activeH(go)) return;
    if (*count >= max) { (*skipped)++; return; }
    (*count)++;
    Buf names = {0}; P cs_ = comps(go);
    for (int i = 0; i < ALEN(cs_); i++) { P c = AT(cs_, i); if (alive(c) && !is(c, kTransform())) bf(&names, "%s%s", names.n ? ", " : "", kname(K(c))); }
    int nc = nchild(t);
    bf(sb, "%*s%s", d * 2, "", uname(go));
    if (names.n) bf(sb, " [%s]", bstr(&names));
    if (!activeH(go)) bs(sb, " (inactive)");
    if (d >= depth && nc > 0) bf(sb, " (+%d children)", nc);
    bs(sb, "\n");
    if (d < depth) for (int i = 0; i < nc; i++) treeWalk(sb, child(t, i), d + 1, depth, max, activeOnly, count, skipped);
}
static int t_scene_tree(void) {
    int depth = aint("depth", 3), max = aint("maxNodes", 200), count = 0, skipped = 0, activeOnly = abool("activeOnly", 1);
    Buf sb = {0};
    char *root = astr("root", NULL);
    if (root) {
        P go = FindGo(root);
        if (!go) fail("GameObject '%s' not found", root);
        treeWalk(&sb, tf(go), 0, depth, max, activeOnly, &count, &skipped);
    } else {
        Vec r = RootObjects();
        for (int i = 0; i < r.n; i++) treeWalk(&sb, tf(r.v[i]), 0, depth, max, activeOnly, &count, &skipped);
    }
    Text("%s", TrimEnd(bstr(&sb)));
    if (skipped) Text("... %d more object(s) not shown (maxNodes) - use root to go deeper", skipped);
    Text("Read state with state_get, e.g. /ObjectName:ComponentType.field");
    return 0;
}
static int HasInputSystem(void) { return CLS("UnityEngine.InputSystem", "InputSystem") != NULL; }
static int t_status(void) {
    Buf sc = {0};
    for (int i = 0, n = sceneCount(); i < n; i++) { Scene s = sceneAt(i); bf(&sc, "%s%s", i ? ", " : "", sceneName(&s)); }
    int plat = IC(int (*)(void), "UnityEngine.Application::get_platform")();
    P pk = CLS("UnityEngine", "RuntimePlatform");
    Text("app: %s %s | unity %s | %s%s", appstr("UnityEngine.Application::get_productName_Injected"), appstr("UnityEngine.Application::get_version_Injected"),
         appstr("UnityEngine.Application::get_unityVersion_Injected"), pk ? tostr(box(pk, &plat)) : F("%d", plat),
         IC(bool (*)(void), "UnityEngine.Debug::get_isDebugBuild")() ? " (development build)" : " (release build)");
    Text("scenes: %s", bstr(&sc));
    float dt = IC(float (*)(void), "UnityEngine.Time::get_unscaledDeltaTime")();
    Text("screen: %dx%d | focused: %s | frame: %d | fps: %s | timeScale: %s", ScreenW(), ScreenH(), IC(bool (*)(void), "UnityEngine.Application::get_isFocused")() ? "True" : "False",
         frameCount(), dt > 0 ? F("%.0f", 1 / dt) : "?", fstr(IC(float (*)(void), "UnityEngine.Time::get_timeScale")()));
#ifdef __APPLE__
    const char *os = ", os (real key events for Input.GetKey)";
#else
    const char *os = "";
#endif
    Text("ui panels: 0 | input methods: event (%s)%s", HasInputSystem() ? "method=input: type_text into uGUI fields only - the native agent cannot send Input System events" : "Input System not active: method=input unavailable", os);
    Text("agent: native, injected into an IL2CPP build");
    if (shelled) Text("computer: walled off by the operating system (files, programs and network outside the game are closed)");
    Text("safety: %s", allowUnsafe ? "UNSAFE - state_set/state_call may reach any .NET API (UDRIVER_UNSAFE=1)"
                                   : "restricted - state_set/state_call limited to game code, UnityEngine and basic types; access token required; loopback only");
    Text("computer access: %s", J->computer ? "on" : shelled ? "off - changes work, the operating system keeps them inside the game" : "off - state_set, state_call and ui_set_value are refused; looking and playing work");
    Text("probes: 0 | recording frames: False (0 stored) | last event seq: %lld", (long long)LastSeq());
    return 0;
}

static struct { uint64_t h; char *name; } scenes[64];
static int nscenes = -1;
static void scene_events(void) {
    struct { uint64_t h; char *name; } now[64]; int n = 0;
    for (int i = 0, c = sceneCount(); i < c && n < 64; i++) { Scene s = sceneAt(i); if (sceneLoaded(&s)) { now[n].h = s.s[0]; now[n].name = sceneName(&s); n++; } }
    if (nscenes >= 0) {
        int kept = 0;
        for (int i = 0; i < nscenes; i++) {
            int still = 0; for (int j = 0; j < n; j++) still |= now[j].h == scenes[i].h;
            if (still) kept++; else emit("scene", F("unloaded %s", scenes[i].name), NULL);
        }
        for (int j = 0; j < n; j++) {
            int old = 0; for (int i = 0; i < nscenes; i++) old |= now[j].h == scenes[i].h;
            if (!old) emit("scene", F("loaded %s (%s)", now[j].name, kept || j ? "Additive" : "Single"), NULL);
        }
    }
    for (int i = 0; i < nscenes; i++) free(scenes[i].name);
    for (int j = 0; j < n; j++) { scenes[j].h = now[j].h; scenes[j].name = strdup(now[j].name); }
    nscenes = n;
}
// ---------------- event queries ----------------

static int isErrCat(const char *c) { return !strcmp(c, "log.error") || !strcmp(c, "log.exception") || !strcmp(c, "log.assert"); }
static Ev evCopy(Ev *e) { Ev r = *e; r.cat = Sn(e->cat, strlen(e->cat)); r.text = Sn(e->text, strlen(e->text)); r.detail = e->detail ? Sn(e->detail, strlen(e->detail)) : NULL; return r; }
static int64_t evFirst(int64_t since) { int64_t f = evNext - Cap; if (f < 1) f = 1; return since + 1 > f ? since + 1 : f; }
static int evQuery(int64_t since, const char **cats, int ncats, const char *contains, int limit, Ev *out) {
    int n = 0;
    pthread_mutex_lock(&evLock);
    for (int64_t s = evFirst(since); s < evNext && n < limit; s++) {
        Ev *e = &evs[s % Cap];
        int ok = !cats;
        for (int c = 0; c < ncats && !ok; c++) ok = catMatch(e->cat, cats[c]);
        if (ok && (!contains || icontains(e->text, contains))) out[n++] = evCopy(e);
    }
    pthread_mutex_unlock(&evLock);
    return n;
}
static char *indent4(const char *s) {
    Buf b = {0};
    for (; *s; s++) { if (*s == '\n') bs(&b, "\n    "); else bput(&b, s, 1); }
    return bstr(&b);
}
static char *evFmt(Ev *e, int detail) {
    char *s = F("#%lld f%d %.2fs [%s] %s", (long long)e->seq, e->frame, e->time, e->cat, OneLine(e->text, 400));
    return detail && e->detail && *e->detail ? F("%s\n    %s", s, indent4(Cut(TrimEnd(F("%s", e->detail)), 1500))) : s;
}
static int byStr(const void *a, const void *b) { return strcmp(*(char **)a, *(char **)b); }
static char *evSummary(int64_t since) {
    Map counts = {0}; Ev errs[5]; int ne = 0; int64_t lost;
    pthread_mutex_lock(&evLock);
    lost = evNext - Cap - (since + 1);
    for (int64_t s = evFirst(since); s < evNext; s++) {
        Ev *e = &evs[s % Cap];
        (*mget(&counts, e->cat))++;
        if (isErrCat(e->cat)) errs[ne++ % 5] = evCopy(e);
    }
    pthread_mutex_unlock(&evLock);
    Buf b = {0};
    bf(&b, "events since #%lld%s: ", (long long)since, lost > 0 ? F(" (%lld older event(s) were overwritten before they could be read)", (long long)lost) : "");
    if (!counts.n) { bs(&b, "none"); return bstr(&b); }
    char **keys = A(counts.n * sizeof(char *));
    memcpy(keys, counts.k, counts.n * sizeof(char *));
    qsort(keys, counts.n, sizeof(char *), byStr);
    for (int i = 0; i < counts.n; i++) bf(&b, "%s x%d  ", keys[i], *mget(&counts, keys[i]));
    for (int i = ne > 5 ? ne - 5 : 0; i < ne; i++) bf(&b, "\n  %s", evFmt(&errs[i % 5], 0));
    return bstr(&b);
}

// ---------------- uGUI ----------------

enum { IdBase = 1000000 };
static int64_t *uiEid;
static int uiN, uiCap;
static GCH hPed, hHover;  // the active press and the hovered object, kept across tool calls
static P kEv(const char *n) { return CLS("UnityEngine.EventSystems", n); }
static P kUI(const char *n) { return CLS("UnityEngine.UI", n); }
static P kGo(void) { return CLS("UnityEngine", "GameObject"); }
static P gh(GCH h) { return h ? il2cpp_gchandle_get_target(h) : NULL; }
static void sh(GCH *h, P o) { if (*h) il2cpp_gchandle_free(*h); *h = o ? il2cpp_gchandle_new(o, false) : 0; }
static int UiId(P go) {
    int64_t e = eid(go);
    for (int i = 0; i < uiN; i++) if (uiEid[i] == e) return IdBase + 1 + i;
    if (uiN == uiCap) uiEid = realloc(uiEid, (uiCap = uiCap ? uiCap * 2 : 256) * sizeof *uiEid);
    uiEid[uiN++] = e;
    return IdBase + uiN;
}
static P uiGet(int id) {
    int i = id - IdBase - 1;
    P go = i >= 0 && i < uiN ? byEid(uiEid[i]) : NULL;
    return alive(go) && is(go, kGo()) && activeH(go) ? go : NULL;
}
static _Noreturn void uiMissing(int id) {
    if (id <= IdBase || id > IdBase + uiN) fail("no element with id #%d - call ui_tree / ui_find", id);
    fail("uGUI element #%d no longer exists or is inactive - call ui_tree / ui_find again", id);
}
static int enabledB(P c) { return !is(c, CLS("UnityEngine", "Behaviour")) || IC(bool (*)(P), "UnityEngine.Behaviour::get_isActiveAndEnabled_Injected")(N(c)); }
static P PropK(P k, const char *name) {  // public instance property without index parameters, like Type.GetProperty
    for (P c = k; c && !isName(c, "System", "Object"); c = il2cpp_class_get_parent(c)) {
        P p = il2cpp_class_get_property_from_name(c, name);
        if (!p) continue;
        P g = il2cpp_property_get_get_method(p), m = g ? g : il2cpp_property_get_set_method(p); uint32_t f;
        if (m && !mstatic(m) && (il2cpp_method_get_flags(m, &f) & 7) == 6 && il2cpp_method_get_param_count(m) == (g ? 0 : 1)) return p;
    }
    return NULL;
}
static char *StrProp(P c, const char *name) {
    P p = PropK(K(c), name), g = p ? getter(p) : NULL, v = NULL;
    if (!g || TRY(v = pget(g, c))) return NULL;
    return v ? TextOf(v) : NULL;
}
static P Selectable(P go) { return comp(go, kUI("Selectable")); }
static char *LabelOn(P go) {
    P gk = kUI("Graphic"), arr = gk ? compsK(go, gk, 0) : NULL;
    for (int i = 0; i < ALEN(arr); i++) {
        P g = AT(arr, i), p = alive(g) ? PropK(K(g), "text") : NULL;
        if (p && ttype(pType(p)) == 0xe) { char *s = StrProp(g, "text"); return s ? s : ""; }
    }
    return NULL;
}
static int Clickable(P go) { return comp(go, kEv("IPointerClickHandler")) || comp(go, kEv("IPointerDownHandler")) || comp(go, kEv("IDragHandler")); }
static int Interactive(P go) { P s = Selectable(go); return s ? UNBOX(bool, call(s, "IsInteractable", 0, NULL)) : Clickable(go); }
static int IsElement(P go) { char *l; return Selectable(go) || comp(go, kUI("ScrollRect")) || Clickable(go) || ((l = LabelOn(go)) && *l); }
static char *UiText(P go) {
    char *own = LabelOn(go);
    if (own && *own) return own;
    if (!Selectable(go) && !Clickable(go)) return "";
    Buf b = {0};
    P arr = compsK(go, kUI("Graphic"), 1);
    for (int i = 0; i < ALEN(arr) && b.n <= 80; i++) {
        P g = goOfC(AT(arr, i));
        if (g == go) continue;
        char *t = LabelOn(g);
        if (t && *t) bf(&b, "%s%s", b.n ? " | " : "", t);
    }
    return bstr(&b);
}
static char *UiValue(P go) {
    P s = Selectable(go);
    if (!s) return NULL;
    if (strstr(kname(K(s)), "InputField")) return StrProp(s, "text");
    char *v = StrProp(s, "isOn");
    return v ? v : StrProp(s, "value");
}
static char *Kind(P go) {
    P s = Selectable(go);
    if (s) return (char *)kname(K(s));
    if (comp(go, kUI("ScrollRect"))) return "ScrollRect";
    P gk = kUI("Graphic"), arr = gk ? compsK(go, gk, 0) : NULL;
    for (int i = 0; i < ALEN(arr); i++) if (PropK(K(AT(arr, i)), "text")) return (char *)kname(K(AT(arr, i)));
    P h = comp(go, kEv("IPointerClickHandler"));
    return h ? (char *)kname(K(h)) : "UIElement";
}
static char *Desc(P go) {
    if (!alive(go)) return "(none)";
    char *t = UiText(go);
    return F("#%d %s \"%s\"%s (uGUI)", UiId(go), Kind(go), uname(go), *t ? F(" \"%s\"", OneLine(t, 40)) : "");
}
static Rct ScreenRect(P go) {
    P t = tf(go);
    if (!isName(K(t), "UnityEngine", "RectTransform")) return (Rct){0};
    P canvas = compUp(go, CLS("UnityEngine", "Canvas")), root = canvas ? mget0(canvas, "get_rootCanvas") : NULL;
    P cam = alive(root) && iget(root, "get_renderMode") != 0 ? mget0(root, "get_worldCamera") : NULL;
    Rct lr; IC(void (*)(P, Rct *), "UnityEngine.RectTransform::get_rect_Injected")(N(t), &lr);
    float x0 = 3.4e38f, y0 = 3.4e38f, x1 = -3.4e38f, y1 = -3.4e38f;
    for (int i = 0; i < 4; i++) {
        V3 l = {lr.x + (i >= 2 ? lr.w : 0), lr.y + (i == 1 || i == 2 ? lr.h : 0), 0}, w, s;
        IC(void (*)(P, V3 *, V3 *), "UnityEngine.Transform::TransformPoint_Injected")(N(t), &l, &w);
        if (alive(cam)) IC(void (*)(P, V3 *, int, V3 *), "UnityEngine.Camera::WorldToScreenPoint_Injected")(N(cam), &w, 2, &s); else s = w;
        x0 = fminf(x0, s.x); y0 = fminf(y0, s.y); x1 = fmaxf(x1, s.x); y1 = fmaxf(y1, s.y);
    }
    return (Rct){x0, ScreenH() - y1, x1 - x0, y1 - y0};
}
static Rct Intersect(Rct a, Rct b) {
    float x0 = fmaxf(a.x, b.x), y0 = fmaxf(a.y, b.y), x1 = fminf(a.x + a.w, b.x + b.w), y1 = fminf(a.y + a.h, b.y + b.h);
    return (Rct){x0, y0, fmaxf(0, x1 - x0), fmaxf(0, y1 - y0)};
}
static V2 center(Rct r) { return (V2){r.x + r.w / 2, r.y + r.h / 2}; }
static P EsCur(void) { P k = kEv("EventSystem"), es = k ? scall(k, "get_current", 0, NULL) : NULL; return alive(es) ? es : NULL; }
static P Es(void) { P es = EsCur(); if (!es) fail("the scene has no active EventSystem, so uGUI cannot receive events"); return es; }
static P Selected(void) { P es = EsCur(), g = es ? mget0(es, "get_currentSelectedGameObject") : NULL; return alive(g) ? g : NULL; }
static char *ULine(P go) {
    Buf b = {0};
    char *t = UiText(go), *v = UiValue(go);
    bf(&b, "#%d %s \"%s\"", UiId(go), Kind(go), uname(go));
    if (*t) bf(&b, " \"%s\"", OneLine(t, 60));
    if (v) bf(&b, " =%s", OneLine(v, 60));
    Rct r = ScreenRect(go);
    bf(&b, " [%.0f,%.0f,%.0f,%.0f]", rint(r.x), rint(r.y), rint(r.w), rint(r.h));
    P s = Selectable(go);
    if (Interactive(go)) bs(&b, " *");
    if (s && !UNBOX(bool, call(s, "IsInteractable", 0, NULL))) bs(&b, " disabled");
    if (alive(go) && Selected() == go) bs(&b, " focused");
    return bstr(&b);
}
static Vec Canvases(void) {
    Vec r = {0}; P ck = CLS("UnityEngine", "Canvas");
    if (!ck) return r;
    P all = IC(P (*)(P, int, int), "UnityEngine.Object::FindObjectsByType")(typeobj(ck), 0, 0);
    int *order = A((ALEN(all) + 1) * sizeof(int));
    for (int i = 0; i < ALEN(all); i++) {
        P c = AT(all, i);
        if (!alive(c) || !bget(c, "get_isRootCanvas") || !enabledB(c)) continue;
        int o = iget(c, "get_sortingOrder"), j = r.n;
        vpush(&r, c);
        for (; j > 0 && order[j - 1] < o; j--) { r.v[j] = r.v[j - 1]; order[j] = order[j - 1]; }
        r.v[j] = c; order[j] = o;
    }
    return r;
}
static void uWalk(Buf *sb, P t, int ind, int io, int max, int *count, int *skipped) {
    P go = goOfC(t);
    if (!activeH(go)) return;
    int print = io ? Interactive(go) : IsElement(go);
    if (print) { if (*count >= max) (*skipped)++; else { bf(sb, "%*s%s\n", ind * 2, "", ULine(go)); (*count)++; } }
    for (int i = 0, n = nchild(t); i < n; i++) uWalk(sb, child(t, i), print && !io ? ind + 1 : ind, io, max, count, skipped);
}
static void collect(P t, Vec *into) {
    if (!activeH(goOfC(t))) return;
    if (IsElement(goOfC(t))) vpush(into, goOfC(t));
    for (int i = 0, n = nchild(t); i < n; i++) collect(child(t, i), into);
}
static Vec UiAll(void) { Vec r = {0}, cs_ = Canvases(); for (int i = 0; i < cs_.n; i++) collect(tf(goOfC(cs_.v[i])), &r); return r; }
static P UiFindFirst(const char *name, const char *text) {
    P partial = NULL; Vec all = UiAll();
    for (int i = 0; i < all.n; i++) {
        P go = all.v[i];
        if (name) {
            if (text && !icontains(UiText(go), text)) continue;
            if (!strcmp(uname(go), name)) return go;
            if (!partial && icontains(uname(go), name)) partial = go;
        } else if (text) {
            char *t = UiText(go);
            if (!*t) continue;
            if (ieq(t, text)) { if (Interactive(go)) return go; if (!partial) partial = go; }
            else if (!partial && icontains(t, text)) partial = go;
        }
    }
    return partial;
}
static V2 Bl(V2 p) { return (V2){p.x, ScreenH() - p.y}; }
static void pset(P o, const char *n, void *a) {  // a: the object for reference types, a pointer to the data for value types
    P s = mth(K(o), F("set_%s", n), 1);
    if (s) { void *av[] = {a}; inv(vmeth(o, s), o, av); return; }
    P f = fld(K(o), F("<%s>k__BackingField", n));
    if (!f) fail("%s.%s is not in this build", kname(K(o)), n);
    il2cpp_field_set_value(o, f, isvt(tk(il2cpp_field_get_type(f))) ? a : (void *)&a);
}
static P pgetv(P o, const char *n) {
    P g = mth(K(o), F("get_%s", n), 0);
    if (g) return inv(vmeth(o, g), o, NULL);
    P f = fld(K(o), F("<%s>k__BackingField", n));
    if (!f) fail("%s.%s is not in this build", kname(K(o)), n);
    return il2cpp_field_get_value_object(f, o);
}
static int pbool(P o, const char *n) { return UNBOX(bool, pgetv(o, n)); }
static P newObj(P k, int argc, void **args) { P o = il2cpp_object_new(k), c = mth(k, ".ctor", argc); if (!c) fail("%s has no constructor with %d argument(s)", kname(k), argc); inv(c, o, args); return o; }
static P newPed(P es) { void *a[] = {es}; return newObj(NEED("UnityEngine.EventSystems", "PointerEventData"), 1, a); }
// ExecuteEvents.Execute: every enabled handler of the interface on go; exceptions are logged, like Unity does
static int Exec(P go, P data, const char *itf, const char *m) {
    P ik = kEv(itf), im = ik ? mth(ik, m, 1) : NULL;
    if (!im || !alive(go)) return 0;
    P all = comps(go); int n = 0;
    for (int i = 0; i < ALEN(all); i++) {
        P c = AT(all, i);
        if (!alive(c) || !is(c, ik) || !enabledB(c)) continue;
        void *a[] = {data};
        if (TRY(inv(vmeth(c, im), c, a))) emit("log.exception", err, NULL);
        n++;
    }
    return n;
}
static int CanHandle(P go, P ik) {
    P all = comps(go);
    for (int i = 0; i < ALEN(all); i++) { P c = AT(all, i); if (alive(c) && is(c, ik) && enabledB(c)) return 1; }
    return 0;
}
static P Handler(P root, const char *itf) {
    P ik = kEv(itf);
    for (P t = ik && alive(root) ? tf(root) : NULL; t; t = parentT(t)) if (CanHandle(goOfC(t), ik)) return goOfC(t);
    return NULL;
}
static P ExecH(P root, P data, const char *itf, const char *m) {
    for (P t = alive(root) ? tf(root) : NULL; t; t = parentT(t)) if (Exec(goOfC(t), data, itf, m)) return goOfC(t);
    return NULL;
}
static int RaycastAll(P ped, P *items) {
    P es = Es(), ra = mth(K(es), "RaycastAll", 2);
    if (!ra) fail("EventSystem.RaycastAll is not in this build");
    P lk = tk(il2cpp_method_get_param(ra, 1)), list = newObj(lk, 0, NULL);
    void *a[] = {ped, list};
    inv(ra, es, a);
    *items = il2cpp_field_get_value_object(fld(lk, "_items"), list);
    return UNBOX(int, il2cpp_field_get_value_object(fld(lk, "_size"), list));
}
static P rrGo(P rr) { return il2cpp_field_get_value_object(fld(K(rr), "m_GameObject"), rr); }
static P Cast(P ped, P *first) {
    P items; int n = RaycastAll(ped, &items);
    *first = n ? ielem(items, 0) : il2cpp_object_new(il2cpp_class_get_element_class(K(items)));
    return n ? rrGo(*first) : NULL;
}
static P Raycast(V2 p) {
    P es = EsCur(), rr;
    if (!es) return NULL;
    P ped = newPed(es); V2 b = Bl(p);
    pset(ped, "position", &b);
    return Cast(ped, &rr);
}
static int isChildOf(P t, P of) { for (; t; t = parentT(t)) if (t == of) return 1; return 0; }
static char *Warnings(P go) {
    Buf b = {0};
    P s = Selectable(go);
    if (s && !UNBOX(bool, call(s, "IsInteractable", 0, NULL))) bs(&b, "WARNING: target is not interactable (disabled).\n");
    Rct r = ScreenRect(go);
    if (r.w < 1 || r.h < 1) bs(&b, "WARNING: target has zero size.\n");
    if (!EsCur()) bs(&b, "WARNING: no EventSystem in the scene.\n");
    else {
        P hit = Raycast(center(r));
        if (!hit) bs(&b, "WARNING: nothing is raycast-hit at the target point (off-screen, masked, or raycastTarget off). Try ui_scroll_to.\n");
        else if (!isChildOf(tf(hit), tf(go)) && !isChildOf(tf(go), tf(hit))) bf(&b, "WARNING: target is covered - the pointer lands on %s instead.\n", Desc(hit));
    }
    return b.n ? TrimEnd(bstr(&b)) : NULL;
}
static void uMove(V2 p) {
    P es = Es(), pressed = gh(hPed), ped = pressed ? pressed : newPed(es), hover = gh(hHover), rr;
    V2 pos = Bl(p), old = UNBOX(V2, pgetv(ped, "position")), d = {pos.x - old.x, pos.y - old.y};
    pset(ped, "delta", &d); pset(ped, "position", &pos);
    P hit = Cast(ped, &rr);
    pset(ped, "pointerCurrentRaycast", il2cpp_object_unbox(rr));
    if (hit != hover) {
        if (alive(hover)) ExecH(hover, ped, "IPointerExitHandler", "OnPointerExit");
        if (hit) ExecH(hit, ped, "IPointerEnterHandler", "OnPointerEnter");
        sh(&hHover, hit);
    }
    P drag = pressed ? pgetv(pressed, "pointerDrag") : NULL;
    if (!alive(drag)) return;
    if (!pbool(pressed, "dragging")) {
        Exec(drag, pressed, "IBeginDragHandler", "OnBeginDrag");
        bool t = 1, f = 0; pset(pressed, "dragging", &t);
        P press = pgetv(pressed, "pointerPress");
        if (alive(press) && press != drag) {  // a drag started on another object than the pressed one cancels the click
            Exec(press, pressed, "IPointerUpHandler", "OnPointerUp");
            pset(pressed, "eligibleForClick", &f); pset(pressed, "pointerPress", NULL); pset(pressed, "rawPointerPress", NULL);
        }
    }
    Exec(drag, pressed, "IDragHandler", "OnDrag");
}
static void SetSel(P es, P go, P data) { void *a[] = {go, data}; call(es, "SetSelectedGameObject", data ? 2 : 1, a); }
static void uDown(V2 p, int button, int clicks) {
    P es = Es(), rr;
    uMove(p);
    V2 pos = Bl(p); P ped = newPed(es);
    int pid = -1 - button; bool t = 1;
    pset(ped, "position", &pos); pset(ped, "pressPosition", &pos); pset(ped, "button", &button); pset(ped, "clickCount", &clicks);
    pset(ped, "pointerId", &pid); pset(ped, "eligibleForClick", &t); pset(ped, "useDragThreshold", &t);
    P hit = Cast(ped, &rr);
    pset(ped, "pointerCurrentRaycast", il2cpp_object_unbox(rr)); pset(ped, "pointerPressRaycast", il2cpp_object_unbox(rr));
    sh(&hPed, ped);
    P sel = hit ? Handler(hit, "ISelectHandler") : NULL;
    if (sel != Selected()) SetSel(es, sel, ped);
    if (!hit) return;
    P pressed = ExecH(hit, ped, "IPointerDownHandler", "OnPointerDown");
    if (!pressed) pressed = Handler(hit, "IPointerClickHandler");
    pset(ped, "pointerPress", pressed); pset(ped, "rawPointerPress", hit);
    P drag = Handler(hit, "IDragHandler");
    pset(ped, "pointerDrag", drag);
    if (drag) Exec(drag, ped, "IInitializePotentialDragHandler", "OnInitializePotentialDrag");
    emit("ui.down", F("down %s button=%d", Desc(pressed ? pressed : hit), button), NULL);
}
static void uUp(V2 p, int button) {
    P ped = gh(hPed), rr;
    if (!ped) return;
    V2 pos = Bl(p);
    pset(ped, "position", &pos);
    P hit = Cast(ped, &rr);
    pset(ped, "pointerCurrentRaycast", il2cpp_object_unbox(rr));
    P press = pgetv(ped, "pointerPress"), drag = pgetv(ped, "pointerDrag");
    if (alive(press)) Exec(press, ped, "IPointerUpHandler", "OnPointerUp");
    P click = hit ? Handler(hit, "IPointerClickHandler") : NULL;
    int dragging = alive(drag) && pbool(ped, "dragging");
    if (alive(press) && press == click && pbool(ped, "eligibleForClick")) {
        Exec(press, ped, "IPointerClickHandler", "OnPointerClick");
        emit("ui.click", F("click %s button=%d", Desc(press), button), NULL);
    } else if (dragging && hit) ExecH(hit, ped, "IDropHandler", "OnDrop");
    if (dragging) Exec(drag, ped, "IEndDragHandler", "OnEndDrag");
    emit("ui.up", F("up %s button=%d", Desc(hit), button), NULL);
    sh(&hPed, NULL);
}
static P ValueOwner(P go, const char *prop, P *pp) {
    P all = comps(go);
    for (int i = 0; i < ALEN(all); i++) {
        P c = AT(all, i);
        if (!alive(c) || is(c, kTransform()) || isName(K(c), "UnityEngine", "CanvasRenderer")) continue;
        const char *names[2] = {prop, NULL};
        if (!prop) { int in = !!strstr(kname(K(c)), "InputField"); names[0] = in ? "text" : "isOn"; names[1] = in ? NULL : "value"; }
        for (int k = 0; k < 2 && names[k]; k++) {
            P x = PropK(K(c), names[k]);
            if (x && il2cpp_property_get_set_method(x)) { *pp = x; return c; }
        }
    }
    return NULL;
}
static void setProp(P c, P p, P v) { P s = il2cpp_property_get_set_method(p), pt = il2cpp_method_get_param(s, 0); void *a[] = {argp(pt, v)}; inv(vmeth(c, s), c, a); }
static char *propText(P c, P p) { P v = pget(getter(p), c); return v ? TextOf(v) : ""; }
static char *UiSetValue(P go, const char *prop, cJSON *value) {
    P p, c = ValueOwner(go, prop, &p);
    if (!c) fail("no component on '%s' has a writable '%s' property", uname(go), prop ? prop : "text / isOn / value");
    Guard(il2cpp_property_get_parent(p), il2cpp_property_get_name(p), "ui_set_value");
    P v = NULL;
    if (TRY(v = To(value, pType(p)))) fail("%s", err);
    setProp(c, p, v);
    char *now = propText(c, p);
    emit("ui.change", F("change %s %s.%s -> %s", Desc(go), kname(K(c)), il2cpp_property_get_name(p), OneLine(now, 80)), NULL);
    return F("%s.%s = %s", kname(K(c)), il2cpp_property_get_name(p), now);
}
static char *UiTypeText(P go, const char *text, int clear, int submit) {
    P p, c = ValueOwner(go, "text", &p);
    if (!c || ttype(pType(p)) != 0xe) fail("'%s' is not a text input (no writable string 'text')", uname(go));
    SetSel(Es(), go, NULL);
    char *now = F("%s%s", clear ? "" : propText(c, p), text);
    setProp(c, p, ms(now));
    emit("ui.change", F("change %s text -> %s", Desc(go), OneLine(now, 80)), NULL);
    if (submit) {
        const char *evn[] = {"onSubmit", "onEndEdit"};
        for (int i = 0; i < 2; i++) {
            P ep = PropK(K(c), evn[i]), ev = ep && getter(ep) ? pget(getter(ep), c) : NULL, im = ev ? mth(K(ev), "Invoke", 1) : NULL;
            if (im && ttype(il2cpp_method_get_param(im, 0)) == 0xe) { void *a[] = {ms(now)}; inv(im, ev, a); }
        }
        void *a[] = {Es()};
        Exec(go, newObj(NEED("UnityEngine.EventSystems", "BaseEventData"), 1, a), "ISubmitHandler", "OnSubmit");
        emit("ui.submit", F("submit %s", Desc(go)), NULL);
    }
    return F("text = %s", now);
}

// ---------------- keys ----------------

enum { KTab = 9, KReturn = 13, KEscape = 27, KSpace = 32, KDelete = 127, KA = 97, KZ = 122, KEnterPad = 271, KUp = 273, KDown, KRight, KLeft, KF1 = 282 };
static int KeyCode(const char *key) {
    char *k = strlen(key) == 1 && isdigit((unsigned char)*key) ? F("Alpha%s", key) : !strncasecmp(key, "Digit", 5) ? F("Alpha%s", key + 5)
            : ieq(key, "Enter") ? "Return" : ieq(key, "Esc") ? "Escape" : (char *)key;
    P kk = CLS("UnityEngine", "KeyCode"), f; void *it = NULL;
    while (kk && !isdigit((unsigned char)*k) && (f = il2cpp_class_get_fields(kk, &it)))
        if ((il2cpp_field_get_flags(f) & 0x40) && ieq(il2cpp_field_get_name(f), k)) { int v = UNBOX(int, fget(f, NULL)); if (v) return v; }
    fail("unknown key '%s'. Use UnityEngine.KeyCode names: Return, Escape, Tab, Space, Backspace, Delete, LeftArrow, W, Alpha1, LeftShift, F5...", key);
}
static int Mods(void) {
    Strs m = astrs("modifiers"); int r = 0;
    for (int i = 0; i < m.n; i++) {
        char *s = m.v[i];
        r |= ieq(s, "shift") ? 1 : ieq(s, "ctrl") || ieq(s, "control") ? 2 : ieq(s, "alt") ? 4 : ieq(s, "cmd") || ieq(s, "command") ? 8 : 0;
    }
    return r;
}
static int keyChar(int c, int mods) { return c >= KA && c <= KZ && (mods & 1) ? c - 32 : c >= KSpace && c < KDelete ? c : 0; }
static int UguiKey(int code, int mods) {
    P go = Selected();
    if (!go || !activeH(go)) return 0;
    P es = Es(); int shift = mods & 1;
    if (code == KTab) {
        P s = Selectable(go), next = NULL;
        if (s) { next = mget0(s, shift ? "FindSelectableOnUp" : "FindSelectableOnDown"); if (!alive(next)) next = mget0(s, shift ? "FindSelectableOnLeft" : "FindSelectableOnRight"); }
        if (alive(next)) SetSel(es, goOfC(next), NULL);
        return 1;
    }
    P all = comps(go);
    for (int i = 0; i < ALEN(all); i++) {
        P c = AT(all, i), kp = alive(c) ? mth(K(c), "KeyPressed", 1) : NULL;
        if (!kp) continue;
        P fp = PropK(K(c), "isFocused"), fv = fp && getter(fp) ? pget(getter(fp), c) : NULL;
        if (!fv || !UNBOX(bool, fv)) break;  // not editing (e.g. after Enter): navigated and submitted like a button
        P ev = newObj(NEED("UnityEngine", "Event"), 0, NULL);
        int type = 4; uint16_t ch = keyChar(code, mods);
        pset(ev, "type", &type); pset(ev, "keyCode", &code); pset(ev, "character", &ch); pset(ev, "modifiers", &mods);
        void *a[] = {ev};
        if (!strcmp(tostr(inv(vmeth(c, kp), c, a)), "Finish")) {
            P so = mth(K(c), "SendOnSubmit", 0), off0 = mth(K(c), "DeactivateInputField", 0), off1 = off0 ? NULL : mth(K(c), "DeactivateInputField", 1);
            if (code != KEscape && so) inv(so, c, NULL);
            bool no = 0; void *b[] = {&no};
            if (off0 || off1) inv(vmeth(c, off0 ? off0 : off1), c, b);
            emit("ui.submit", F("%s%s", code == KEscape ? "cancel " : "submit ", Desc(go)), NULL);
        }
        return 1;
    }
    void *a[] = {es};
    if (code == KReturn || code == KEnterPad || code == KSpace) {
        Exec(go, newObj(NEED("UnityEngine.EventSystems", "BaseEventData"), 1, a), "ISubmitHandler", "OnSubmit");
        emit("ui.submit", F("submit %s", Desc(go)), NULL);
    } else if (code == KEscape) Exec(go, newObj(NEED("UnityEngine.EventSystems", "BaseEventData"), 1, a), "ICancelHandler", "OnCancel");
    else {
        int dir = code == KUp ? 1 : code == KDown ? 3 : code == KLeft ? 0 : code == KRight ? 2 : 4;
        if (dir == 4) return 0;
        P ax = newObj(NEED("UnityEngine.EventSystems", "AxisEventData"), 1, a);
        pset(ax, "moveDir", &dir);
        Exec(go, ax, "IMoveHandler", "OnMove");
    }
    return 1;
}
static P getKeyM;
static int Held(int code) {  // Input.GetKey(KeyCode); -1 when the build has no such method
    if (!getKeyM) {
        P ik = CLS("UnityEngine", "Input"), m; void *it = NULL;
        while (ik && (m = il2cpp_class_get_methods(ik, &it)))
            if (!strcmp(il2cpp_method_get_name(m), "GetKey") && il2cpp_method_get_param_count(m) == 1 && il2cpp_class_is_enum(tk(il2cpp_method_get_param(m, 0)))) getKeyM = m;
        if (!getKeyM) return -1;
    }
    void *a[] = {&code}; P r = NULL;
    return TRY(r = inv(getKeyM, NULL, a)) ? 0 : UNBOX(bool, r);
}
#ifdef __APPLE__
#define OS_KEYS 1
static int MacKey(int c) {
    static const char chars[] = "asdfhgzxcv\0bqweryt123465=97-80]ou[ip\0lj'k;\\,/nm.\t `";
    for (int i = 0; c > 0 && c < 128 && i < (int)sizeof chars - 1; i++) if (chars[i] && chars[i] == c) return i;
    static const int f[] = {0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F};
    if (c >= KF1 && c < KF1 + 12) return f[c - KF1];
    static const int map[][2] = {{KReturn, 0x24}, {KEnterPad, 0x4C}, {KEscape, 0x35}, {8, 0x33}, {KDelete, 0x75}, {KLeft, 0x7B}, {KRight, 0x7C}, {KDown, 0x7D}, {KUp, 0x7E},
                                 {278, 0x73}, {279, 0x77}, {280, 0x74}, {281, 0x79}, {304, 0x38}, {303, 0x3C}, {306, 0x3B}, {305, 0x3E}, {308, 0x3A}, {307, 0x3D}, {310, 0x37}, {309, 0x36}};
    for (size_t i = 0; i < sizeof map / sizeof *map; i++) if (map[i][0] == c) return map[i][1];
    fail("key %d cannot be sent as a real key event; letters, digits, punctuation, Space, Return, Escape, Tab, Backspace, Delete, arrows, Home/End/PageUp/PageDown, Shift/Control/Alt and F1-F12 can", c);
}
// Into the game's own event queue: needs no Accessibility permission and works in the background.
static void OsSend(int code, int down, int mods) {
    int own = code == 303 || code == 304 ? 1 : code == 305 || code == 306 ? 2 : code == 307 || code == 308 ? 4 : code == 309 || code == 310 ? 8 : 0, m = down ? mods | own : mods;
    CGEventRef e = CGEventCreateKeyboardEvent(NULL, (CGKeyCode)MacKey(code), down);
    if (own) CGEventSetType(e, kCGEventFlagsChanged);
    CGEventSetFlags(e, (m & 1 ? 0x20000 : 0) | (m & 2 ? 0x40000 : 0) | (m & 4 ? 0x80000 : 0) | (m & 8 ? 0x100000 : 0));
    UniChar ch = keyChar(code, mods);
    if (ch) CGEventKeyboardSetUnicodeString(e, 1, &ch);
    id ns = ((id (*)(Class, SEL, CGEventRef))objc_msgSend)(objc_getClass("NSEvent"), sel_registerName("eventWithCGEvent:"), e);
    id app = ((id (*)(Class, SEL))objc_msgSend)(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
    ((void (*)(id, SEL, id, BOOL))objc_msgSend)(app, sel_registerName("postEvent:atStart:"), ns, NO);
    CFRelease(e);
}
#else
#define OS_KEYS 0
static void OsSend(int code, int down, int mods) { fail("method=os works only in macOS and Windows game builds"); }
#endif

// ---------------- UI and input tools ----------------

typedef struct { P go; V2 s; } Tgt;
static Tgt uTarget(int withText) {
    Tgt t = {0};
    char *name = astr("name", NULL), *text = withText ? astr("text", NULL) : NULL;
    if (arg("id")) {
        int id = aint("id", 0);
        if (!(t.go = uiGet(id))) uiMissing(id);
    } else if (name || text) {
        if (!(t.go = UiFindFirst(name, text))) fail("no visible element with name='%s' text='%s' - check ui_tree / ui_find", name ? name : "", text ? text : "");
    }
    if (t.go) {
        Rct r = ScreenRect(t.go), c = Intersect(r, (Rct){0, 0, ScreenW(), ScreenH()});
        t.s = center(c.w > 0 && c.h > 0 ? c : r);
    } else if (arg("x") && arg("y")) { t.s = (V2){anum("x", 0), anum("y", 0)}; t.go = Raycast(t.s); }
    else fail("specify a target: id, name, text, or x and y");
    return t;
}
static void saveT(Tgt t) { J->i[7] = t.go ? eid(t.go) : 0; J->d[6] = t.s.x; J->d[7] = t.s.y; }
static Tgt loadT(void) { P o = J->i[7] ? byEid(J->i[7]) : NULL; return (Tgt){alive(o) ? o : NULL, {J->d[6], J->d[7]}}; }
static char *Where(Tgt t) { return F("%sat (%.0f,%.0f)", t.go ? F("%s ", Desc(t.go)) : "", rint(t.s.x), rint(t.s.y)); }
static char *InMethod(void) {
    char *m = astr("method", "event");
    if (!strcmp(m, "input") && HasInputSystem()) fail("method=input is not available: the native agent cannot send Input System events (that needs the UDriver package in the game). Use method=event.");
    if (!strcmp(m, "input")) fail("method=input needs the Input System package with 'Active Input Handling' set to Input System or Both. Use method=event.");
    if (strcmp(m, "event") && strcmp(m, "set")) fail("unknown method '%s'", m);
    return m;
}
static int Btn(void) { char *b = astr("button", "left"); return !strcmp(b, "right") ? 1 : !strcmp(b, "middle") ? 2 : 0; }
static void Feedback(int64_t seq, int expectClick) {
    const char *cats[] = {"ui.click", "ui.change", "ui.submit", "ui.focus"};
    Ev e[8];
    for (int i = 0, n = evQuery(seq, cats, 4, NULL, 8, e); i < n; i++) Text("  %s", evFmt(&e[i], 0));
    Text("seq_before=%lld | %s", (long long)seq, evSummary(seq));
    if (expectClick && !evQuery(seq, cats, 1, NULL, 1, e))
        Text("NOTE: no ClickEvent was produced. The element may be covered, disabled, or not a UI Toolkit element - check ui_inspect, or retry with method=input.");
}
static int t_ui_tree(void) {
    int max = aint("maxNodes", 300), io = abool("interactiveOnly", 0), count = 0, skipped = 0;
    Buf sb = {0};
    if (arg("root") || arg("id")) {
        int id = aint(arg("id") ? "id" : "root", 0);
        P go = uiGet(id);
        if (!go) uiMissing(id);
        uWalk(&sb, tf(go), 0, io, max, &count, &skipped);
    } else {
        Vec cs_ = Canvases();
        if (!cs_.n) { Text("No UI found: no active UIDocument (UI Toolkit) and no active Canvas (uGUI). If the game draws its UI another way (IMGUI, world-space objects), use screenshot + screen_pick + x,y with method=input."); return 0; }
        for (int i = 0; i < cs_.n; i++) {
            P c = cs_.v[i];
            bf(&sb, "canvas \"%s\" (uGUI, %s, order %d)\n", uname(c), tostr(mget0(c, "get_renderMode")), iget(c, "get_sortingOrder"));
            uWalk(&sb, tf(goOfC(c)), 1, io, count + (max - count > 0 ? max - count : 0) / (cs_.n - i), &count, &skipped);
        }
        if (skipped && cs_.n > 1) bf(&sb, "(maxNodes is shared equally between the %d panels and canvases above)\n", cs_.n);
    }
    Text("%s", TrimEnd(bstr(&sb)));
    if (skipped) Text("... %d more element(s) not shown (maxNodes). Raise maxNodes, or narrow with root=<id of a panel element or canvas child> / interactiveOnly / ui_find.", skipped);
    return 0;
}
static int t_ui_find(void) {
    char *name = astr("name", NULL), *text = astr("text", NULL), *cls = astr("cls", NULL), *type = astr("type", NULL);
    if (!name && !text && !cls && !type) fail("give at least one of: name, text, cls, type");
    int limit = aint("limit", 30), n = 0, more = 0;
    Vec all = cls ? (Vec){0} : UiAll();
    for (int i = 0; i < all.n; i++) {
        P go = all.v[i];
        if ((name && !icontains(uname(go), name)) || (type && !ieq(Kind(go), type))) continue;
        if (text && !icontains(UiText(go), text) && !icontains(UiValue(go), text)) continue;
        if (n < limit) { Text("%s  path=%s", ULine(go), PathOf(go)); n++; } else more++;
    }
    Text("%d match(es)%s", n, more ? F(", %d more not shown", more) : "");
    return 0;
}
static int t_ui_click(void) {
    int count = aint("count", 1), hold = aint("holdMs", 0), button = Btn();
    count = count < 1 ? 1 : count > 3 ? 3 : count; hold = hold < 0 ? 0 : hold > 10000 ? 10000 : hold;
    char *m = InMethod();
    int now = !strcmp(m, "event") && hold == 0;
    BEGIN
    {
        Tgt t = uTarget(1);
        saveT(t);
        J->i[0] = LastSeq();
        char *w = t.go ? Warnings(t.go) : NULL;
        if (w) Text("%s", w);
        if (t.go || gh(hPed)) uMove(t.s);
    }
    if (!now) YIELD(1);
    for (J->i[1] = 1; J->i[1] <= count; J->i[1]++) {
        { Tgt t = loadT(); if (t.go) uDown(t.s, button, (int)J->i[1]); }
        if (hold > 0) { J->d[0] = now_ms() + hold; while (now_ms() < J->d[0]) YIELD(1); }
        else if (!now) YIELD(1);
        { Tgt t = loadT(); if (t.go || gh(hPed)) uUp(t.s, button); }
        if (!now) YIELD(1);
    }
    YIELD(1);
    {
        Tgt t = loadT();
        Text("%s%s via %s", count > 1 ? "double-clicked " : hold > 0 ? F("long-pressed (%d ms) ", hold) : "clicked ", Where(t), m);
        Feedback(J->i[0], button == 0 && !strcmp(m, "event") && t.go);
    }
    END
}
static int t_pointer(void) {
    char *m = InMethod(), *action = astr("action", "");
    int button = Btn();
    BEGIN
    {
        Tgt t = uTarget(1);
        saveT(t);
        J->i[0] = LastSeq();
        if (!strcmp(action, "down")) { if (t.go || gh(hPed)) uMove(t.s); }
        else if (!strcmp(action, "up")) { if (t.go || gh(hPed)) uUp(t.s, button); }
        else if (!strcmp(action, "move")) { if (t.go || gh(hPed)) uMove(t.s); }
        else fail("action must be down, move or up");
    }
    if (!strcmp(action, "down")) {
        YIELD(1);
        { Tgt t = loadT(); if (t.go) { uDown(t.s, button, 1); J->i[2] = 1; } }
    }
    YIELD(1);
    Text("pointer %s %s via %s", action, Where(loadT()), m);
    Feedback(J->i[0], 0);
    END
}
static int t_ui_set_value(void) {
    BEGIN
    {
        Tgt t = uTarget(1);
        J->i[0] = LastSeq();
        if (!t.go || arg("x")) fail("ui_set_value needs an element (id, name or text)");
        cJSON *v = cJSON_GetObjectItemCaseSensitive(J->args, "value");
        Text("set %s: %s", Desc(t.go), OneLine(UiSetValue(t.go, astr("property", NULL), v), 200));
    }
    YIELD(1);
    Feedback(J->i[0], 0);
    END
}
static int t_ui_focus(void) {
    BEGIN
    {
        Tgt t = uTarget(1);
        if (!t.go || arg("x")) fail("ui_focus needs an element (id, name or text)");
        SetSel(Es(), t.go, NULL);
    }
    YIELD(1);
    Text("selected uGUI object is now: %s", Desc(Selected()));
    END
}
static int t_type_text(void) {
    char *text = astr("text", NULL);
    if (!text) fail("text is required");
    int in = !strcmp(astr("method", "event"), "input") && HasInputSystem();
    if (!in) InMethod();
    BEGIN
    {
        J->i[0] = LastSeq();
        P go = arg("id") || arg("name") ? uTarget(0).go : Selected();
        if (!go) fail("nothing is focused - pass id/name of the field to type into");
        Text("typed into %s (%s): %s", Desc(go), in ? "uGUI fields do not read Input System text and the native agent cannot send it, so the field was filled by setting the text; onValueChanged fires" : "uGUI fields are filled by setting the text; onValueChanged fires", OneLine(UiTypeText(go, text, abool("clear", 0), abool("submit", 0)), 200));
    }
    YIELD(1);
    Feedback(J->i[0], 0);
    END
}
static int t_key(void) {
    char *key = astr("key", NULL);
    if (!key) fail("key is required");
    char *m = astr("method", "auto"), *action = astr("action", "press");
    if (strcmp(m, "auto") && strcmp(m, "os")) InMethod();
    if (!strcmp(m, "os") && !OS_KEYS) fail("method=os works only in macOS and Windows game builds");
    if (strcmp(action, "press") && strcmp(action, "down") && strcmp(action, "up")) fail("action must be press, down or up");
    int hold = aint("holdMs", 0), mods = Mods(), code = KeyCode(key), up = !strcmp(action, "up"), down = !strcmp(action, "down");
    hold = hold < 0 ? 0 : hold > 10000 ? 10000 : hold;
    BEGIN
    J->i[0] = LastSeq();
    if (strcmp(m, "os") && !up && UguiKey(code, mods)) {
        YIELD(1);
        Text("key %s sent to the selected uGUI object %s%s", key, Desc(Selected()), !strcmp(m, "auto") ? " (to reach gameplay code instead, use method=os)" : "");
        Feedback(J->i[0], 0);
        return 0;
    }
    if (strcmp(m, "os") && !OS_KEYS)
        fail("nothing can take this key: no UI element is focused or selected (ui_focus one first), the Input System package is not active, and real key events are posted only by macOS and Windows game builds");
    J->i[1] = -1;
    if (!up) {
        OsSend(code, 1, mods);
        J->i[2] = 1;
        J->d[0] = now_ms() + 500;
        while ((J->i[1] = Held(code)) == 0 && now_ms() < J->d[0]) YIELD(1);
        if (!down && hold > 0) { J->d[0] = now_ms() + hold; while (now_ms() < J->d[0]) YIELD(1); }
    }
    if (!down) { OsSend(code, 0, mods); J->i[2] = 0; YIELD(1); }
    YIELD(1);
    Text("key %s %s via os%s", action, key, J->i[1] < 0 ? "" : J->i[1] ? "; the game's Input.GetKey saw it" : "; WARNING: the game's Input.GetKey did not see it - bring the game window to the front and retry");
    Feedback(J->i[0], 0);
    END
}
static void release_input(Job *j) {
    if (j->sub) { release_input(j->sub); job_unref(j->sub); j->sub = NULL; }
    if (!j->cancelled) return;
    if (j->fn == t_pointer && j->i[2]) sh(&hPed, NULL);
    if (j->fn == t_key && j->i[2]) { Job *o = J; J = j; TRY(OsSend(KeyCode(astr("key", "")), 0, 0)); J = o; }
}

// ---------------- locate ----------------

static P MainCamera(void) {
    P c = G(IC(intptr_t (*)(void), "UnityEngine.Camera::get_main_Injected")());
    if (alive(c)) return c;
    int n = IC(int (*)(void), "UnityEngine.Camera::GetAllCamerasCount")();
    P arr = il2cpp_array_new(NEED("UnityEngine", "Camera"), n), best = NULL; float bd = 0;
    IC(int (*)(P), "UnityEngine.Camera::GetAllCamerasImpl_Injected")(arr);
    for (int i = 0; i < n; i++) {
        P x = AT(arr, i);
        float d = alive(x) ? IC(float (*)(P), "UnityEngine.Camera::get_depth_Injected")(N(x)) : 0;
        if (alive(x) && (!best || d > bd)) { best = x; bd = d; }
    }
    return best;
}
static int t_locate(void) {
    char *path = astr("path", NULL);
    P go = path ? FindGo(path) : NULL;
    if (!go) fail("GameObject '%s' not found - use scene_tree", path ? path : "");
    Text("%s | active=%s | world position %s", PathOf(go), activeH(go) ? "True" : "False", v3s(position(tf(go))));
    if (isName(K(tf(go)), "UnityEngine", "RectTransform") && compUp(go, CLS("UnityEngine", "Canvas")) && kEv("EventSystem")) {
        Rct rc = ScreenRect(go); V2 c = center(rc);
        Text("uGUI rect on screen: [%.0f,%.0f,%.0f,%.0f], center (%.0f,%.0f)", rint(rc.x), rint(rc.y), rint(rc.w), rint(rc.h), rint(c.x), rint(c.y));
        return 0;
    }
    P cam = MainCamera();
    if (!cam) { Text("no enabled camera - cannot project to the screen"); return 0; }
    P rk = CLS("UnityEngine", "Renderer"), rend = rk ? compDown(go, rk) : NULL;
    V3 world = position(tf(go)), sp;
    if (rend) { V3 b[2]; IC(void (*)(P, V3 *), "UnityEngine.Renderer::get_bounds_Injected")(N(rend), b); world = b[0]; }
    IC(void (*)(P, V3 *, int, V3 *), "UnityEngine.Camera::WorldToScreenPoint_Injected")(N(cam), &world, 2, &sp);
    int W = ScreenW(), H = ScreenH(), on = sp.z > 0 && sp.x >= 0 && sp.x <= W && sp.y >= 0 && sp.y <= H;
    Text("screen point (camera '%s'): x=%.0f y=%.0f depth=%s%s. Click it with ui_click x,y method=input (may be occluded: check screen_pick).", uname(cam), rint(sp.x), rint(H - sp.y),
         g3(rint(sp.z * 100) / 100), sp.z <= 0 ? " - BEHIND the camera" : on ? " - on screen" : " - outside the screen");
    return 0;
}

// ---------------- screenshot, time ----------------

static char *b64(const unsigned char *p, size_t n) {
    static const char t[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    char *r = A((n + 2) / 3 * 4 + 1), *o = r;
    for (size_t i = 0; i < n; i += 3) {
        unsigned v = p[i] << 16 | (i + 1 < n ? p[i + 1] << 8 : 0) | (i + 2 < n ? p[i + 2] : 0);
        *o++ = t[v >> 18]; *o++ = t[v >> 12 & 63]; *o++ = i + 1 < n ? t[v >> 6 & 63] : '='; *o++ = i + 2 < n ? t[v & 63] : '=';
    }
    return r;
}
static int t_screenshot(void) {
    if (arg("camera")) fail("camera is not supported by the native agent: screenshot captures the whole screen");
    BEGIN
    YIELD(2);
    {
        P tex = G(IC(intptr_t (*)(int, int), "UnityEngine.ScreenCapture::CaptureScreenshotAsTexture_Injected")(1, 1));
        if (!alive(tex)) fail("the screen could not be captured");
        int w = IC(int (*)(P), "UnityEngine.Texture::GetDataWidth_Injected")(N(tex)), h = IC(int (*)(P), "UnityEngine.Texture::GetDataHeight_Injected")(N(tex));
        P px = IC(P (*)(P, int), "UnityEngine.Texture2D::GetPixels32_Injected")(N(tex), 0);
        IC(void (*)(P, float), "UnityEngine.Object::Destroy_Injected")(N(tex), 0);
        uint32_t *src = (uint32_t *)ARR(px), *img = src;
        int maxW = aint("maxWidth", 1024), nw = w, nh = h;
        if (w > maxW) {  // nearest-neighbour downscale; rows stay bottom-up as the encoder expects
            nw = maxW < 16 ? 16 : maxW; nh = (int)rint(h * (nw / (double)w)); if (nh < 1) nh = 1;
            img = A((size_t)nw * nh * 4);
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) img[(size_t)y * nw + x] = src[(size_t)(y * h / nh) * w + x * w / nw];
        }
        int q = aint("quality", 75), size = nw * nh * 4;
        q = q < 10 ? 10 : q > 100 ? 100 : q;
        void *jpg = IC(void *(*)(void *, int *, int, uint32_t, uint32_t, uint32_t, int), "UnityEngine.ImageConversion::UnsafeEncodeNativeArrayToJPG")(img, &size, 8, nw, nh, nw * 4, q);
        if (!jpg) fail("JPEG encoding failed");
        char *data = b64(jpg, size);
        IC(void (*)(void *, int), "Unity.Collections.LowLevel.Unsafe.UnsafeUtility::Free")(jpg, 4);
        double scale = nw / (double)ScreenW();
        Text("screen %dx%d, image %dx%d, frame %d%s", ScreenW(), ScreenH(), nw, nh, frameCount(),
             fabs(scale - 1) > 0.001 ? F(". Image is scaled: screen_coord = image_coord * %s", g3(1 / scale)) : ". Image coords = screen coords");
        if (abool("annotate", 0)) Text("annotate is not available in the native agent");
        Image(data, "image/jpeg");
    }
    END
}
static float savedScale = 1;
static char timeBy[64];
static float timeScale(void) { return IC(float (*)(void), "UnityEngine.Time::get_timeScale")(); }
static void setScale(float s) { IC(void (*)(float), "UnityEngine.Time::set_timeScale")(s); }
static int t_time_control(void) {
    char *action = astr("action", "status");
    BEGIN
    if (*timeBy && strcmp(timeBy, J->client)) Text("note: time was last changed by %s", timeBy);
    if (strcmp(action, "status")) snprintf(timeBy, sizeof timeBy, "%s", J->client);
    if (!strcmp(action, "pause")) { if (timeScale() > 0) savedScale = timeScale(); setScale(0); }
    else if (!strcmp(action, "resume")) setScale(savedScale > 0 ? savedScale : 1);
    else if (!strcmp(action, "scale")) { setScale(fmax(0, anum("scale", 1))); if (timeScale() > 0) savedScale = timeScale(); }
    else if (!strcmp(action, "step")) {
        J->i[0] = aint("frames", 1); J->i[0] = J->i[0] < 1 ? 1 : J->i[0] > 600 ? 600 : J->i[0];
        setScale(savedScale > 0 ? savedScale : 1);
        for (J->i[1] = 0; J->i[1] < J->i[0]; J->i[1]++) YIELD(1);
        setScale(0);
        Text("advanced %d frame(s)", (int)J->i[0]);
    } else if (strcmp(action, "status")) fail("action must be pause, resume, step, scale or status");
    Text("timeScale=%s frame=%d time=%.3f%s", fstr(timeScale()), frameCount(), IC(float (*)(void), "UnityEngine.Time::get_time")(),
         timeScale() == 0 ? " (PAUSED: scaled time is frozen; code using unscaled time or Update without deltaTime still runs)" : "");
    END
}

// ---------------- events, errors, memory, batch ----------------

static int t_wait_for(void) {
    char *cat = astr("event", NULL), *contains = astr("contains", NULL), *uiName = astr("ui_name", NULL), *uiText = astr("ui_text", NULL), *path = astr("state", NULL), *eq = astr("equals", NULL);
    int gone = abool("gone", 0), ui = uiName || uiText, evCond = cat || (contains && !ui && !path), frames = aint("frames", 0), wait = aint("ms", 0);
    BEGIN
    J->i[0] = arg("sinceSeq") ? (int64_t)anum("sinceSeq", 0) : LastSeq();
    J->i[1] = now_ms();
    if (!evCond && !ui && !path) {
        if (frames <= 0 && wait <= 0) fail("give at least one condition: event/contains, ui_name/ui_text, state, frames or ms");
        for (J->i[2] = 0; J->i[2] < frames; J->i[2]++) YIELD(1);
        J->d[0] = now_ms() + wait;
        while (wait > 0 && now_ms() < J->d[0]) YIELD(1);
        Text("waited %s%s%s", frames > 0 ? F("%d frame(s)", frames) : "", frames > 0 && wait > 0 ? " + " : "", wait > 0 ? F("%d ms", wait) : "");
        Text("%s", evSummary(J->i[0]));
        return 0;
    }
    for (;;) {
        {
            char *hit = NULL, *last = NULL;
            if (evCond) {
                Ev e; const char *cats[] = {cat};
                if (evQuery(J->i[0], cat ? cats : NULL, 1, contains, 1, &e)) hit = F("event %s", evFmt(&e, 1));
            }
            if (!hit && ui) {
                P go = UiFindFirst(uiName, uiText);
                if (!!go != gone) hit = gone ? "element is gone" : F("element present: %s", ULine(go));
            }
            if (!hit && path) {
                P v = NULL;
                int bad = TRY(v = Resolve(path));
                if (!bad) bad = TRY(last = Fmt(v, 0, 10, 0));
                if (bad) last = F("<error: %s>", err);
                char *a = last, *b = eq ? F("%s", eq) : NULL;
                while (*a == '"') a++;
                if (b) { while (*b == '"') b++; for (int n = (int)strlen(b); n && b[n - 1] == '"';) b[--n] = 0; }
                a = F("%s", a); for (int n = (int)strlen(a); n && a[n - 1] == '"';) a[--n] = 0;
                if (!bad && (!b || ieq(a, b))) hit = F("state %s = %s", path, last);
            }
            long long ms = now_ms() - J->i[1];
            if (hit || ms > aint("timeoutMs", 10000)) {
                Text("%s", hit ? F("MATCHED after %lld ms: %s", ms, hit) : F("TIMEOUT after %lld ms: condition not met%s", ms, last ? F(" (last value of %s: %s)", path, last) : ""));
                Text("%s", evSummary(J->i[0]));
                return 0;
            }
        }
        YIELD(1);
    }
    END
}
static int byLast(const void *a, const void *b) { double x = ((Grp *)a)->last, y = ((Grp *)b)->last; return x < y ? 1 : x > y ? -1 : 0; }
static int t_errors(void) {
    int full = abool("full", 0), limit = aint("limit", 20), offset = aint("offset", 0), n = 0, dropped;
    char *contains = astr("contains", NULL);
    if (offset < 0) offset = 0;
    pthread_mutex_lock(&evLock);
    Grp *gs = A((ngroups + 1) * sizeof(Grp));
    for (int i = 0; i < ngroups; i++) {
        Grp g = groups[i];
        if (contains && !icontains(F("%s %s %s", g.type, g.msg, g.stack), contains)) continue;
        g.type = F("%s", g.type); g.top = F("%s", g.top); g.msg = F("%s", g.msg); g.stack = F("%s", g.stack);
        gs[n++] = g;
    }
    dropped = ungrouped;
    pthread_mutex_unlock(&evLock);
    qsort(gs, n, sizeof *gs, byLast);
    int end = offset + limit < n ? offset + limit : n;
    for (int i = offset; i < end; i++) {
        Grp *g = &gs[i];
        Text("%dx %s%s | first %.2fs f%d, last %.2fs f%d\n    %s%s", g->count, g->type, *g->top ? F(" at %s", g->top) : "", g->first, g->firstFrame, g->last, g->lastFrame,
             OneLine(g->msg, 400), full && *g->stack ? F("\n    %s", indent4(TrimEnd(g->stack))) : "");
    }
    Text("%s%s%s", !n ? F("no errors%s logged since the driver started (or the last events_config clear=true)", contains ? F(" matching '%s'", contains) : "")
                      : F("shown %d-%d of %d error group(s), newest first%s", offset + 1 < end ? offset + 1 : end, end, n, end < n ? F("; next page: offset=%d", end) : ""),
         dropped > 0 ? F("; %d error(s) not grouped (limit %d groups)", dropped, MaxGroups) : "", full || !n ? "" : "; full=true adds the whole stack of the latest occurrence");
    return 0;
}
static cJSON *lnum(const char *icall) { P f = icall_opt(icall); return f ? cJSON_CreateNumber((double)((int64_t (*)(void))f)()) : cJSON_CreateNull(); }
static int t_memory_summary(void) {
    cJSON *o = cJSON_CreateObject(), *u = cJSON_AddObjectToObject(o, "unity"), *m = cJSON_AddObjectToObject(o, "managed"), *gc = cJSON_CreateArray();
    cJSON_AddItemToObject(u, "used", lnum("UnityEngine.Profiling.Profiler::GetTotalAllocatedMemoryLong"));
    cJSON_AddItemToObject(u, "reserved", lnum("UnityEngine.Profiling.Profiler::GetTotalReservedMemoryLong"));
    cJSON_AddItemToObject(u, "graphicsDriver", lnum("UnityEngine.Profiling.Profiler::GetAllocatedMemoryForGraphicsDriver"));
    cJSON_AddItemToObject(m, "used", lnum("UnityEngine.Profiling.Profiler::GetMonoUsedSizeLong"));
    cJSON_AddItemToObject(m, "heap", lnum("UnityEngine.Profiling.Profiler::GetMonoHeapSizeLong"));
    for (int g = 0; g < 3; g++) { void *a[] = {&g}; P r = NULL; P gk = CLS("System", "GC"); cJSON_AddItemToArray(gc, !gk || TRY(r = scall(gk, "CollectionCount", 1, a)) ? cJSON_CreateNull() : cJSON_CreateNumber(UNBOX(int, r))); }
    cJSON_AddItemToObject(m, "gcCollections", gc);
    cJSON_AddNumberToObject(o, "frame", frameCount());
    cJSON_AddStringToObject(o, "note", "the native agent reports no profiler counters, renderTextures or audioClips; gcCollections is null when the build strips GC.CollectionCount");
    char *s = cJSON_PrintUnformatted(o);
    Text("%s", s);
    free(s); cJSON_Delete(o);
    return 0;
}
static Step find_tool(const char *name, int computer);
static void appendJob(Job *s) {
    flush(J); flush(s);
    cJSON *c;
    cJSON_ArrayForEach(c, s->content) cJSON_AddItemToArray(J->content, cJSON_Duplicate(c, 1));
    J->isError |= s->isError;
}
static int t_batch(void) {
    cJSON *calls = arg("calls");
    int n = cJSON_IsArray(calls) ? cJSON_GetArraySize(calls) : 0, stop = abool("stopOnError", 1), first = aint("first", 0);
    char *output = astr("output", "all");
    BEGIN
    if (!n) fail("calls must be a non-empty array of {tool, args}");
    if (n > 50) fail("at most 50 calls per batch");
    if (strcmp(output, "all") && strcmp(output, "last") && strcmp(output, "errors")) fail("output must be all, last or errors");
    for (J->i[0] = 0; J->i[0] < n; J->i[0]++) {
        {
            cJSON *c = cJSON_GetArrayItem(calls, (int)J->i[0]), *tj = cJSON_GetObjectItemCaseSensitive(c, "tool"), *aj = cJSON_GetObjectItemCaseSensitive(c, "args");
            const char *tool = cJSON_IsString(tj) ? tj->valuestring : NULL;
            Step fn = tool && strcmp(tool, "batch") ? find_tool(tool, J->computer) : NULL;
            Job *s = job_new(tool ? tool : "", cJSON_IsObject(aj) ? cJSON_Duplicate(aj, 1) : cJSON_CreateObject(), fn, 1), *o = J;
            memcpy(s->client, J->client, sizeof s->client);
            J = s;
            Text("--- [%d] %s", first + (int)o->i[0], tool ? tool : "");
            if (!fn) Error(F("unknown tool '%s'", tool ? tool : ""));
            J = o; J->sub = s;
        }
        while (J->sub->fn && !J->cancelled && (J->i[1] = runStep(J->sub))) YIELD((int)J->i[1]);
        {
            Job *s = J->sub;
            J->sub = NULL;
            release_input(s);
            int quiet = cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(cJSON_GetArrayItem(calls, (int)J->i[0]), "quiet")), bad = s->isError;
            if (J->i[2]) { job_unref((Job *)(intptr_t)J->i[2]); J->i[2] = 0; }
            if (bad || (!strcmp(output, "all") && !quiet)) { appendJob(s); job_unref(s); }
            else if (!strcmp(output, "last")) J->i[2] = (intptr_t)s;
            else job_unref(s);
            if (bad && stop) { Text("batch stopped at call %d (stopOnError)", first + (int)J->i[0]); return 0; }
        }
    }
    if (J->i[2]) { Job *s = (Job *)(intptr_t)J->i[2]; J->i[2] = 0; appendJob(s); job_unref(s); }
    END
}

// No Harmony in IL2CPP, so nothing holds the game's own code back once an agent changed the game (a path, URL or command
// written into a field). While computer access is off only looking and playing are offered, unless the operating system walls the
// game off (shelled); typing is play, like a player's.
static int t_no_computer(void) {
    Error(F("Not done: '%s' changes the game, and Computer access is off. In an IL2CPP game UDriver cannot stop the game's own code "
            "from carrying such a change to your files, programs or network. Looking, clicking, keys and typing still work. "
            "The owner can turn Computer access on in the UDriver panel.", J->tool));
    return 0;
}

static const struct { const char *name; Step fn; int change; } Tools[] = {
    {"status", t_status}, {"scene_tree", t_scene_tree}, {"locate", t_locate}, {"state_get", t_state_get}, {"state_set", t_state_set, 1}, {"state_call", t_state_call, 1},
    {"state_query", t_state_query}, {"types_find", t_types_find}, {"type_members", t_type_members}, {"ui_tree", t_ui_tree}, {"ui_find", t_ui_find},
    {"ui_click", t_ui_click}, {"ui_set_value", t_ui_set_value, 1}, {"ui_focus", t_ui_focus}, {"type_text", t_type_text}, {"screenshot", t_screenshot},
    {"key", t_key}, {"pointer", t_pointer}, {"memory_summary", t_memory_summary}, {"time_control", t_time_control}, {"wait_for", t_wait_for},
    {"batch", t_batch}, {"errors", t_errors},
};
static Step find_tool(const char *name, int computer) {
    for (size_t i = 0; i < sizeof Tools / sizeof *Tools; i++) if (!strcmp(Tools[i].name, name)) return Tools[i].change && !computer && !shelled ? t_no_computer : Tools[i].fn;
    return NULL;
}
