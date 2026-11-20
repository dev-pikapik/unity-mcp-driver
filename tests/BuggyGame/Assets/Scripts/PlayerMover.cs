using UnityEngine;

public class PlayerMover : MonoBehaviour
{
    public float speed = 1.5f;
    public float range = 3f;
    public Vector3 position;
    public Vector3 velocity;

    void Start()
    {
        position = transform.position;
    }

    void Update()
    {
        var target = new Vector3(Mathf.Sin(Time.time * speed) * range, position.y, position.z);
        velocity = (target - position) / Time.deltaTime;
        position += velocity * Time.deltaTime;
        transform.position = position;
    }
}
