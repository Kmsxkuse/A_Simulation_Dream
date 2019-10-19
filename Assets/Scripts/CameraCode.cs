using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

public class CameraCode : MonoBehaviour
{
    private Bounds _bounds;
    private InputAction.CallbackContext _dragDelta;
    private Vector2 _keyDeltas, _dragOrigin, _delta;

    private bool _keyHeld, _dragLock;
    private Camera _mainCam;
    private int _zoomDirection;
    public GameObject map;

    private void Awake()
    {
        _bounds = map.GetComponent<Renderer>().bounds;
        _mainCam = Camera.main;
    }

    public void DetectKeyPress(InputAction.CallbackContext context)
    {
        _keyHeld = context.performed;
        _keyDeltas = context.ReadValue<Vector2>() / 2;
    }

    public void DetectDrag(InputAction.CallbackContext context)
    {
        _dragLock = context.performed;

        if (_dragLock)
            _dragOrigin = _mainCam.ScreenToWorldPoint(Input.mousePosition);
    }

    public void DetectClick(InputAction.CallbackContext context)
    {
        Debug.Log($"{context.started}, {context.performed}, {context.canceled}");
    }

    public void DetectZoom(InputAction.CallbackContext context)
    {
        if (context.performed)
            _zoomDirection = (int) math.sign(context.ReadValue<float>());
        if (context.canceled)
            _zoomDirection = 0;
    }

    private void Update()
    {
        var orthographicSize = _mainCam.orthographicSize;
        _mainCam.orthographicSize = Mathf.Clamp(-_zoomDirection *
                                                orthographicSize / 6 + orthographicSize, 0.2f, 6);

        if (!_keyHeld && !_dragLock)
            return;

        var oldPosition = transform.position;
        var loopChecker = false;

        if (_dragLock)
            _delta = _dragOrigin - (Vector2) _mainCam.ScreenToWorldPoint(Input.mousePosition);

        // Looper code
        var looper = math.abs(oldPosition.x) - _bounds.max.x;
        if (looper >= 0 && !_dragLock)
        {
            loopChecker = true;
            looper = (_bounds.max.x - looper) * -oldPosition.x / math.abs(oldPosition.x);
        }
        else
        {
            looper = oldPosition.x + (_dragLock ? _delta.x : _keyDeltas.x);
        }

        oldPosition = new float3(
            looper,
            math.clamp(oldPosition.y + (_dragLock ? _delta.y : _keyDeltas.y),
                orthographicSize - _bounds.max.y, _bounds.max.y - orthographicSize),
            -10);

        if (!_dragLock && !loopChecker)
            oldPosition = math.lerp(transform.position, oldPosition, Time.deltaTime * orthographicSize * 10);

        transform.position = oldPosition;
    }
}