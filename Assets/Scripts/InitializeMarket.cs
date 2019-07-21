using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class InitializeMarket : ComponentSystem
{
    public static Entity[] GoodsMostLogic;

    protected override void OnCreateManager()
    {
        var goods = new Dictionary<string, Good>();

        foreach (var goodPath in Directory.EnumerateFiles(
            Path.Combine(Application.streamingAssetsPath, "Goods"), "*.json"))
        {
            var jsonGood = JsonGood.CreateFromJson(goodPath);
            goods.Add(jsonGood.name, new Good(goods.Count, jsonGood));
        }

        var goodArray = goods.Values.ToList();
        var logicGoodsCounter = new int[goods.Count];
        GoodsMostLogic = new Entity[goods.Count];

        var logic = new Dictionary<string, Entity>();
        var startingInventories = new Dictionary<string, NativeArray<InvContent>>();
        var startingStatistics = new Dictionary<string, NativeArray<InvStats>>();

        var cofAndLg = new NativeArray<CostOfLivingAndLimitGood>(goods.Count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);

        using (var possibleDeltas = new NativeList<PossibleDelta>(Allocator.Temp))
        using (var deltas = new NativeList<DeltaValue>(Allocator.Temp))
        {
            foreach (var logicPath in Directory.EnumerateFiles(
                Path.Combine(Application.streamingAssetsPath, "Factories"), "*.json"))
            {
                var jsonLogic = JsonLogic.CreateFromJson(logicPath);
                var currentLogic = EntityManager.CreateEntity(typeof(Logic));
                EntityManager.SetComponentData(currentLogic, new Logic(logic.Count));

                // Resetting array
                for (var goodIndex = 0; goodIndex < cofAndLg.Length; goodIndex++)
                    cofAndLg[goodIndex] = new CostOfLivingAndLimitGood(0);
                // First pass for costs of living
                foreach (var livingCost in jsonLogic.costOfLiving)
                    cofAndLg[goods[livingCost.name].Index] = new CostOfLivingAndLimitGood(livingCost.quantity);
                // Second pass for limit goods
                foreach (var goodsLimit in jsonLogic.limitGoods)
                {
                    var index = goods[goodsLimit.name].Index;
                    var placeholder = cofAndLg[index];
                    placeholder.LimitGoods = goodsLimit.quantity;
                    cofAndLg[index] = placeholder;
                }

                EntityManager.AddBuffer<CostOfLivingAndLimitGood>(currentLogic).AddRange(cofAndLg);

                var idealQuantity = new NativeArray<IdealQuantity>(goods.Count, Allocator.Temp);
                foreach (var value in jsonLogic.idealQuantity)
                    idealQuantity[goods[value.name].Index] = value.quantity;
                EntityManager.AddBuffer<IdealQuantity>(currentLogic).AddRange(idealQuantity);
                idealQuantity.Dispose();

                var currentInventory = new NativeArray<InvContent>(goods.Count, Allocator.Temp);
                var currentStatistic = new NativeArray<InvStats>(goods.Count, Allocator.Temp);

                // Assigning starting good inventory
                foreach (var value in jsonLogic.startQuantity)
                {
                    var targetGood = goods[value.name];
                    currentInventory[targetGood.Index] = new InvContent(value.quantity, targetGood.InitialCost);
                }

                // Assigning rest of goods prices and statistics
                for (var index = 0; index < currentInventory.Length; index++)
                {
                    var targetStats = new InvStats(goodArray[index].InitialCost);
                    currentStatistic[index] = targetStats;

                    var targetInv = currentInventory[index];
                    if (targetInv.RecordedPrice > 0.1)
                        continue;
                    targetInv.RecordedPrice = goodArray[index].InitialCost;
                    currentInventory[index] = targetInv;
                }

                startingInventories.Add(jsonLogic.name, currentInventory);
                startingStatistics.Add(jsonLogic.name, currentStatistic);
                // Starting values
                EntityManager.AddBuffer<InvContent>(currentLogic).AddRange(currentInventory);
                EntityManager.AddBuffer<InvStats>(currentLogic).AddRange(currentStatistic);

                possibleDeltas.Clear();
                deltas.Clear();
                foreach (var possibilities in jsonLogic.possibleDeltas)
                {
                    var consumeStart = deltas.Length;
                    foreach (var value in possibilities.consumes)
                        deltas.Add(new DeltaValue(goods[value.name].Index, value.quantity, value.possibility));

                    var produceStart = deltas.Length;
                    foreach (var value in possibilities.produces)
                    {
                        var goodsIndex = goods[value.name].Index;
                        deltas.Add(new DeltaValue(goodsIndex, value.quantity, value.possibility));

                        if (logicGoodsCounter[goods[value.name].Index] > math.abs(value.quantity))
                            continue;

                        logicGoodsCounter[goodsIndex] = value.quantity;
                        GoodsMostLogic[goodsIndex] = currentLogic;
                    }

                    possibleDeltas.Add(new int3(consumeStart, produceStart, deltas.Length));
                }

                EntityManager.AddBuffer<PossibleDelta>(currentLogic).AddRange(possibleDeltas);
                EntityManager.AddBuffer<DeltaValue>(currentLogic).AddRange(deltas);

                logic.Add(jsonLogic.name, currentLogic);
            }
        }

        History.AssignLogs(goods.Count, logic.Count);

        // DEBUG

        for (var counter = 0; counter < 20; counter++)
            CreateAgent("Farm", 50);

        CreateAgent("Mine", 100);
        CreateAgent("Ore_Refinery", 100);
        CreateAgent("Ore_Refinery", 100);
        CreateAgent("Ore_Refinery", 100);
        CreateAgent("Sawmill", 100);
        CreateAgent("Sawmill", 100);
        CreateAgent("Smithy", 100);
        CreateAgent("Smithy", 100);

        for (var counter = 0; counter < 40; counter++)
            CreateAgent("Peasant", 20);

        foreach (var startingInventory in startingInventories)
            startingInventory.Value.Dispose();
        foreach (var startingStatistic in startingStatistics)
            startingStatistic.Value.Dispose();
        cofAndLg.Dispose();

        void CreateAgent(string factoryType, int wealth)
        {
            var currentAgent = EntityManager.CreateEntity(typeof(Agent), typeof(Wallet));
            EntityManager.SetComponentData(currentAgent, new Agent(logic[factoryType]));
            EntityManager.SetComponentData(currentAgent, new Wallet(wealth));
            EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories[factoryType]);
            EntityManager.AddBuffer<InvStats>(currentAgent).AddRange(startingStatistics[factoryType]);
            Debug.Log(factoryType + " index: " + currentAgent.Index);
        }
    }

    protected override void OnUpdate()
    {
        Enabled = false;
    }
}