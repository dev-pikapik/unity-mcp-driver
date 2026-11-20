using UnityEngine;
using UnityEngine.InputSystem;

public static class Arena
{
    public const float Half = 25f;
    public static int Seed, Zombies, Alive, Chickens;
    public static bool OldInput;
    public static PlayerController Player;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Seed = Arg("-seed", 1);
        Zombies = Arg("-zombies", 20);
        Alive = Arg("-alive", 4);
        Chickens = Arg("-chickens", 8);
        OldInput = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-oldinput") >= 0;
        Random.InitState(Seed);
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(50, -30, 0);

        Block("Ground", new Vector3(0, -0.1f, 0), new Vector3(Half * 2, 0.2f, Half * 2), new Color(0.5f, 0.5f, 0.5f));
        var wall = new Color(0.25f, 0.3f, 0.4f);
        Block("Wall", new Vector3(0, 1.5f, Half), new Vector3(Half * 2, 3, 0.5f), wall);
        Block("Wall", new Vector3(0, 1.5f, -Half), new Vector3(Half * 2, 3, 0.5f), wall);
        Block("Wall", new Vector3(Half, 1.5f, 0), new Vector3(0.5f, 3, Half * 2), wall);
        Block("Wall", new Vector3(-Half, 1.5f, 0), new Vector3(0.5f, 3, Half * 2), wall);
        foreach (var p in new[] { new Vector3(9, 2, 9), new Vector3(-9, 2, 9), new Vector3(9, 2, -9), new Vector3(-9, 2, -9) })
            Block("Pillar", p, new Vector3(2, 4, 2), new Color(0.2f, 0.4f, 0.85f));
        Physics.SyncTransforms();
        for (int i = 0; i < 6; i++)
        {
            Vector3 p;
            do p = RandomPoint(20); while (p.magnitude < 4 || Physics.CheckBox(p + Vector3.up * 0.35f, new Vector3(1.2f, 0.25f, 1.2f)));
            Block("Crate", p + Vector3.up * 0.3f, new Vector3(1, 0.6f, 1), new Color(0.75f, 0.45f, 0.15f));
            Physics.SyncTransforms();
        }

        var player = new GameObject("Player") { tag = "Player" };
        player.transform.position = new Vector3(0, 0.05f, 0);
        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.4f;
        cc.center = new Vector3(0, 0.9f, 0);
        var camGo = new GameObject("Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(player.transform, false);
        camGo.transform.localPosition = new Vector3(0, 1.6f, 0);
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
        cam.fieldOfView = 70;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<Gun>();
        Player = player.AddComponent<PlayerController>();

        new GameObject("GameStats").AddComponent<GameStats>();

        for (int i = 1; i <= Chickens; i++)
        {
            var c = Block("Chicken_" + i.ToString("00"), RandomPoint(20) + Vector3.up * 0.2f, Vector3.one * 0.4f, Color.white);
            c.tag = "Chicken";
            c.AddComponent<Rigidbody>().isKinematic = true;
            var comb = Block("Comb", Vector3.zero, Vector3.one, Color.red);
            comb.transform.SetParent(c.transform, false);
            comb.transform.localPosition = new Vector3(0, 0.62f, 0.2f);
            comb.transform.localScale = new Vector3(0.2f, 0.25f, 0.45f);
            c.AddComponent<Chicken>();
        }

        Debug.Log($"[Arena] start seed={Seed} zombies={Zombies} alive={Alive} chickens={Chickens} input={(OldInput ? "old" : "new")}");
    }

    static int Arg(string name, int fallback)
    {
        var a = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : fallback;
    }

    public static Vector3 RandomPoint(float r) => new Vector3(Random.Range(-r, r), 0, Random.Range(-r, r));

    public static GameObject Block(string name, Vector3 pos, Vector3 size, Color color, PrimitiveType type = PrimitiveType.Cube)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.position = pos;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().material = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = color };
        return go;
    }

    public static bool Blocked(Vector3 from, Vector3 dir, float radius, float distance) =>
        Physics.SphereCast(from, radius, dir, out var h, distance, ~0, QueryTriggerInteraction.Ignore)
        && h.collider.attachedRigidbody == null && !(h.collider is CharacterController);

    public static void SpawnZombie()
    {
        var player = Player.transform.position;
        for (int tries = 0; tries < 30; tries++)
        {
            float a = Random.Range(-22f, 22f);
            var p = Random.Range(0, 4) switch
            {
                0 => new Vector3(a, 0.9f, 22),
                1 => new Vector3(a, 0.9f, -22),
                2 => new Vector3(22, 0.9f, a),
                _ => new Vector3(-22, 0.9f, a),
            };
            if (Vector3.Distance(new Vector3(p.x, player.y, p.z), player) < 15 || Physics.CheckSphere(p, 0.6f)) continue;
            var stats = GameStats.I;
            stats.zombiesSpawned++;
            var z = Block("Zombie_" + stats.zombiesSpawned.ToString("00"), p, new Vector3(0.8f, 0.9f, 0.8f), new Color(0.2f, 0.7f, 0.2f), PrimitiveType.Capsule);
            z.tag = "Zombie";
            z.AddComponent<Rigidbody>().isKinematic = true;
            z.AddComponent<Zombie>();
            Debug.Log($"[Arena] spawned {z.name} at ({p.x:0.0}, {p.z:0.0}) {stats.zombiesSpawned}/{Zombies}");
            return;
        }
    }
}

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public float health = 100, pitch, sensitivity = 0.1f, walkSpeed = 4, runSpeed = 7, jumpHeight = 1.2f, gravity = -9.81f;
    public bool alive = true;
    CharacterController cc;
    Transform cam;
    Gun gun;
    float vy, nextHurtLog;
    InputAction move, look, fire, jump, run;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        cam = transform.Find("Camera");
        gun = cam.GetComponent<Gun>();
        move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
        move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        move.AddBinding("<Gamepad>/leftStick");
        look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta", expectedControlType: "Vector2");
        look.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=20,y=20)");
        fire = new InputAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
        fire.AddBinding("<Gamepad>/rightTrigger");
        jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jump.AddBinding("<Gamepad>/buttonSouth");
        run = new InputAction("Run", InputActionType.Button, "<Keyboard>/leftShift");
        run.AddBinding("<Gamepad>/leftStickPress");
    }

    void OnEnable() { move.Enable(); look.Enable(); fire.Enable(); jump.Enable(); run.Enable(); }
    void OnDisable() { move.Disable(); look.Disable(); fire.Disable(); jump.Disable(); run.Disable(); }

    void OnApplicationFocus(bool focused)
    {
        Cursor.lockState = focused ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !focused;
    }

    void Update()
    {
        if (!alive || GameStats.I.gameOver) return;
        Vector2 m, l;
        bool f, j, r;
        if (Arena.OldInput)
        {
            m = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
            l = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10;
            f = Input.GetButtonDown("Fire1");
            j = Input.GetButtonDown("Jump");
            r = Input.GetKey(KeyCode.LeftShift);
        }
        else
        {
            m = move.ReadValue<Vector2>();
            l = look.ReadValue<Vector2>();
            f = fire.WasPressedThisFrame();
            j = jump.WasPressedThisFrame();
            r = run.IsPressed();
        }

        transform.Rotate(0, l.x * sensitivity, 0);
        pitch = Mathf.Clamp(pitch + l.y * sensitivity, -80, 80);
        cam.localEulerAngles = new Vector3(-pitch, 0, 0);
        if (f) gun.Fire();
        if (cc.isGrounded && vy < 0) vy = -2;
        if (j) Jump();
        vy += gravity * Time.deltaTime;
        var dir = Vector3.ClampMagnitude(transform.right * m.x + transform.forward * m.y, 1);
        cc.Move((dir * (r ? runSpeed : walkSpeed) + Vector3.up * vy) * Time.deltaTime);
    }

    public void Jump()
    {
        if (alive && cc.isGrounded) vy = Mathf.Sqrt(2 * -gravity * jumpHeight);
    }

    public void Damage(float amount)
    {
        if (!alive) return;
        health = Mathf.Max(0, health - amount);
        GameStats.I.playerHealth = health;
        if (Time.time >= nextHurtLog)
        {
            nextHurtLog = Time.time + 1;
            Debug.Log($"[Arena] player damaged hp={health:0.0}");
        }
        if (health > 0) return;
        alive = false;
        GameStats.I.End(false);
    }
}

public class Gun : MonoBehaviour
{
    public int ammo = 12, magazine = 12;
    public float cooldown = 0.15f, reloadTime = 1f, range = 60;
    float next, reloadAt, hideTracer;
    LineRenderer tracer;

    void Awake()
    {
        tracer = gameObject.AddComponent<LineRenderer>();
        tracer.material = new Material(Shader.Find("Sprites/Default"));
        tracer.startColor = tracer.endColor = new Color(1, 0.9f, 0.2f);
        tracer.widthMultiplier = 0.03f;
        tracer.enabled = false;
    }

    void Update()
    {
        if (tracer.enabled && Time.time >= hideTracer) tracer.enabled = false;
        if (ammo == 0 && Time.time >= reloadAt) ammo = magazine;
    }

    public bool Fire()
    {
        var stats = GameStats.I;
        if (ammo <= 0 || Time.time < next || stats.gameOver) return false;
        next = Time.time + cooldown;
        if (--ammo == 0) reloadAt = Time.time + reloadTime;
        stats.shotsFired++;
        var end = transform.position + transform.forward * range;
        if (Physics.Raycast(transform.position, transform.forward, out var h, range, ~0, QueryTriggerInteraction.Ignore))
        {
            end = h.point;
            var zombie = h.collider.GetComponentInParent<Zombie>();
            var chicken = h.collider.GetComponentInParent<Chicken>();
            if (zombie || chicken) stats.shotsHit++;
            if (zombie) zombie.Hit(1);
            if (chicken) chicken.Hit(1);
        }
        tracer.SetPosition(0, transform.position + transform.right * 0.15f - transform.up * 0.15f + transform.forward * 0.3f);
        tracer.SetPosition(1, end);
        tracer.enabled = true;
        hideTracer = Time.time + 0.05f;
        return true;
    }
}

public class Zombie : MonoBehaviour
{
    public int health = 3;
    public float speed = 2.2f, reach = 1.3f, damagePerSecond = 10;
    static readonly float[] Turns = { 0, 45, -45, 90, -90 };

    void Update()
    {
        if (GameStats.I.gameOver) return;
        var to = Arena.Player.transform.position - transform.position;
        to.y = 0;
        if (to.magnitude <= reach)
        {
            Arena.Player.Damage(damagePerSecond * Time.deltaTime);
            return;
        }
        var feet = transform.position - Vector3.up * 0.4f;
        foreach (var turn in Turns)
        {
            var dir = Quaternion.Euler(0, turn, 0) * to.normalized;
            if (Arena.Blocked(feet, dir, 0.35f, 1f)) continue;
            transform.position += dir * speed * Time.deltaTime;
            transform.rotation = Quaternion.LookRotation(dir);
            return;
        }
    }

    public void Hit(int damage)
    {
        health -= damage;
        if (health > 0) return;
        Destroy(gameObject);
        GameStats.I.ZombieKilled(name);
    }
}

public class Chicken : MonoBehaviour
{
    public int health = 1;
    public float speed = 1.5f, dashSpeed = 4;
    Vector3 target;
    float current;

    void Start() => Pick();

    void Pick()
    {
        var p = transform.position;
        bool dash = Random.value < 0.25f;
        current = dash ? dashSpeed : speed;
        target = dash ? new Vector3(-p.x, 0, -p.z) + Arena.RandomPoint(5) : p + Arena.RandomPoint(6);
        target = new Vector3(Mathf.Clamp(target.x, -22, 22), p.y, Mathf.Clamp(target.z, -22, 22));
    }

    void Update()
    {
        var to = target - transform.position;
        if (to.magnitude < 0.3f || Arena.Blocked(transform.position, to.normalized, 0.15f, 0.5f))
        {
            Pick();
            return;
        }
        transform.rotation = Quaternion.LookRotation(to);
        transform.position += to.normalized * current * Time.deltaTime;
    }

    public void Hit(int damage)
    {
        health -= damage;
        if (health > 0) return;
        Destroy(gameObject);
        GameStats.I.chickensHit++;
        Debug.Log($"[Arena] chicken hit {name} total={GameStats.I.chickensHit}");
    }
}

public class GameStats : MonoBehaviour
{
    public static GameStats I;
    public int zombiesKilled, zombiesSpawned, chickensHit, shotsFired, shotsHit;
    public float timeAlive, playerHealth = 100;
    public bool gameOver, won;
    GUIStyle hud, big;

    void Awake() => I = this;

    void Update()
    {
        if (gameOver) return;
        timeAlive += Time.deltaTime;
        if (zombiesSpawned < Arena.Zombies && zombiesSpawned - zombiesKilled < Arena.Alive) Arena.SpawnZombie();
    }

    public void ZombieKilled(string zombie)
    {
        zombiesKilled++;
        Debug.Log($"[Arena] zombie killed {zombie} {zombiesKilled}/{Arena.Zombies}");
        if (zombiesKilled >= Arena.Zombies) End(true);
    }

    public void End(bool win)
    {
        if (gameOver) return;
        gameOver = true;
        won = win;
        Debug.Log($"[Arena] {(win ? "WIN" : "DEAD")} zombies={zombiesKilled}/{Arena.Zombies} chickens={chickensHit} shots={shotsFired} hits={shotsHit} hp={playerHealth:0} time={timeAlive:0.0}s");
    }

    void OnGUI()
    {
        hud ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        big ??= new GUIStyle(GUI.skin.label) { fontSize = 120, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        var gun = Arena.Player.GetComponentInChildren<Gun>();
        GUI.Box(new Rect(10, 10, 720, 40), GUIContent.none);
        GUI.Label(new Rect(20, 14, 710, 34), $"Zombies {zombiesKilled}/{Arena.Zombies}  Chickens hit {chickensHit}  HP {playerHealth:0}  Ammo {gun.ammo}/{gun.magazine}  Time {timeAlive:0.0}s", hud);
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        GUI.DrawTexture(new Rect(cx - 10, cy - 1, 20, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - 1, cy - 10, 2, 20), Texture2D.whiteTexture);
        if (!gameOver) return;
        big.normal.textColor = won ? Color.green : Color.red;
        GUI.Label(new Rect(0, 0, Screen.width, Screen.height), won ? "WIN" : "DEAD", big);
    }
}
