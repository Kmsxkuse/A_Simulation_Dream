using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class History : MonoBehaviour
{
    public static int GoodsCount, LogicsCount;
    private List<Queue<float3>> _history;
    private List<Transform[]> _lineLabels;

    private List<LineRenderer[]> _lines;

    private MarketSystem _marketSystem;
    private List<float> _minimums, _maximums;

    private int _tickCounter;
    private IEnumerator _updateLoop;

    private TextMeshPro _xAxis, _yAxis, _xOrigin, _yOrigin;

    [SerializeField] private GameObject linePrefab, lineLabel, xAxis, yAxis, xOrigin, yOrigin;

    public static void AssignLogs(int goodsNum, int logicNum)
    {
        GoodsCount = goodsNum;
        LogicsCount = logicNum;
    }

    private void Start()
    {
        _marketSystem = World.Active.GetOrCreateSystem<MarketSystem>();

        _xAxis = xAxis.GetComponent<TextMeshPro>();
        _yAxis = yAxis.GetComponent<TextMeshPro>();
        _xOrigin = xOrigin.GetComponent<TextMeshPro>();
        _yOrigin = yOrigin.GetComponent<TextMeshPro>();

        var colourValues = new[]
        {
            "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF", "000000",
            "800000", "008000", "000080", "808000", "800080", "008080", "808080",
            "C00000", "00C000", "0000C0", "C0C000", "C000C0", "00C0C0", "C0C0C0",
            "400000", "004000", "000040", "404000", "400040", "004040", "404040",
            "200000", "002000", "000020", "202000", "200020", "002020", "202020",
            "600000", "006000", "000060", "606000", "600060", "006060", "606060",
            "A00000", "00A000", "0000A0", "A0A000", "A000A0", "00A0A0", "A0A0A0",
            "E00000", "00E000", "0000E0", "E0E000", "E000E0", "00E0E0", "E0E0E0"
        };

        _lines = new List<LineRenderer[]>
        {
            new LineRenderer[LogicsCount]
        };
        _lineLabels = new List<Transform[]>
        {
            new Transform[LogicsCount]
        };
        for (var chartIndex = 0; chartIndex < _lines.Count; chartIndex++)
        {
            var chartType = _lines[chartIndex];
            var labels = _lineLabels[chartIndex];
            for (var index = 0; index < chartType.Length; index++)
            {
                var targetLine = Instantiate(linePrefab, transform).GetComponent<LineRenderer>();
                ColorUtility.TryParseHtmlString("#" + colourValues[index], out var lineColor);
                targetLine.endColor = targetLine.startColor = lineColor;
                chartType[index] = targetLine;
                var targetLabel = Instantiate(lineLabel, transform);
                targetLabel.GetComponent<TextMeshPro>().text = InitializeMarket.LogicNames[index];
                labels[index] = targetLabel.transform;
            }
        }

        _history = new List<Queue<float3>>
        {
            new Queue<float3>()
        };

        _minimums = new List<float>
        {
            9999
        };
        _maximums = new List<float>
        {
            -9999
        };

        _tickCounter = 0;
    }

    private void Update()
    {
        if (_updateLoop != null)
            return;

        _updateLoop = WaitSeconds();
        StartCoroutine(_updateLoop);
    }

    private IEnumerator WaitSeconds()
    {
        //Debug.Break();
        _tickCounter++;

        yield return new WaitForSecondsRealtime(0.005f);

        // Ask history section
        ProcessArrayToLines(_marketSystem.ProfitsHistory, 0);
        _marketSystem.Update();
        _updateLoop = null;
    }

    private void ProcessArrayToLines(in NativeArray<float> targetArray, in int listIndex)
    {
        var offset = 0f;
        if (_tickCounter > 20)
        {
            offset = -0.475f;
            transform.Translate(offset, 0, 0);
        }

        var circularQueue = _history[listIndex];
        var removedMax = -9999f;
        var removedMin = 9999f;
        while (circularQueue.Count > 0 && _tickCounter - circularQueue.Peek().y > 19)
        {
            var removed = circularQueue.Dequeue();
            removedMax = math.max(removed.x, removedMax);
            removedMin = math.min(removed.x, removedMin);
        }

        var targetList = _lines[listIndex];
        for (var index = 0; index < targetList.Length; index++)
        {
            var targetValue = targetArray[index];

            // Calculating new extremes
            _minimums[listIndex] = math.clamp(math.min(_minimums[listIndex], targetValue), -9999, 0);
            _maximums[listIndex] = math.clamp(math.max(_maximums[listIndex], targetValue), 0.01f, 9999);

            _yAxis.text = _maximums[listIndex].ToString(CultureInfo.InvariantCulture);
            _yOrigin.text = _minimums[listIndex].ToString(CultureInfo.InvariantCulture);

            circularQueue.Enqueue(new float3(targetValue, _tickCounter, index));

            // Removing old extreme
            if (_tickCounter > 20)
            {
                if (math.abs(_maximums[listIndex] - removedMax) < 0.05f)
                    _maximums[listIndex] = circularQueue.Max(x => x.x);
                else if (math.abs(_minimums[listIndex] - removedMin) < 0.05f)
                    _minimums[listIndex] = circularQueue.Min(x => x.x);

                _xOrigin.text = (_tickCounter - 20).ToString(CultureInfo.InvariantCulture);
                _xAxis.text = _tickCounter.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                targetList[index].positionCount++;
            }
        }

        var pointsList = new List<List<Vector3>>(targetList.Length);
        pointsList.AddRange(targetList.Select(t => new List<Vector3>(21 * targetList.Length)));

        foreach (var point in circularQueue)
            pointsList[(int) point.z].Add(new Vector3(point.y / 19f * 0.902f - 0.45f,
                point.x / (_maximums[listIndex] + math.abs(_minimums[listIndex])) * 0.9f - 0.45f));

        for (var index = 0; index < pointsList.Count; index++)
        {
            var labelY = _lineLabels[listIndex][index];
            labelY.localPosition = new Vector3(labelY.localPosition.x - offset / 10, pointsList[index].Last().y);
            _lineLabels[listIndex][index] = labelY;

            targetList[index].SetPositions(pointsList[index].ToArray());
        }
    }
}