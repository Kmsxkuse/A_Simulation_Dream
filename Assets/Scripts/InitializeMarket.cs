using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class InitializeMarket : ComponentSystem
{
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

        History.AssignLogs(goods.Count);
        foreach (var index in goods)
        {
            // Start history charts with 1 fake buy/sell bid
            History.Prices[index.Value.Index].Add(index.Value.InitialCost);
            History.Asks[index.Value.Index].Add(index.Value.InitialCost);
            History.Bids[index.Value.Index].Add(index.Value.InitialCost);
            History.Trades[index.Value.Index].Add(index.Value.InitialCost);
        }

        var logic = new Dictionary<string, Entity>();
        var startingInventories = new Dictionary<string, NativeArray<InvContent>>();
        var startingStatistics = new Dictionary<string, NativeArray<InvStats>>();
        var startingUsedSpace = new Dictionary<string, int>();

        foreach (var logicPath in Directory.EnumerateFiles(
            Path.Combine(Application.streamingAssetsPath, "Factories"), "*.json"))
        {
            var jsonLogic = JsonLogic.CreateFromJson(logicPath);
            var currentLogic = EntityManager.CreateEntity(typeof(Logic));
            EntityManager.SetComponentData(currentLogic, new Logic(logic.Count));

            using (var costOfLiving = new NativeList<CostOfLiving>(Allocator.Temp))
            {
                foreach (var value in jsonLogic.costOfLiving)
                    costOfLiving.Add(new CostOfLiving(goods[value.name].Index, value.quantity));
                EntityManager.AddBuffer<CostOfLiving>(currentLogic).AddRange(costOfLiving);
            }

            using (var limitGoods = new NativeList<LimitGood>(Allocator.Temp))
            {
                foreach (var value in jsonLogic.limitGoods)
                    limitGoods.Add(new LimitGood(goods[value.name].Index, value.quantity));
                EntityManager.AddBuffer<LimitGood>(currentLogic).AddRange(limitGoods);
            }

            var idealQuantity = new NativeArray<IdealQuantity>(goods.Count, Allocator.Temp);
            foreach (var value in jsonLogic.idealQuantity)
                idealQuantity[goods[value.name].Index] = value.quantity;
            EntityManager.AddBuffer<IdealQuantity>(currentLogic).AddRange(idealQuantity);
            idealQuantity.Dispose();

            var currentInventory = new NativeArray<InvContent>(goods.Count, Allocator.Temp);
            var currentStatistic = new NativeArray<InvStats>(goods.Count, Allocator.Temp);

            var sizeCounter = 0;
            // Assigning starting good inventory
            foreach (var value in jsonLogic.startQuantity)
            {
                var targetGood = goods[value.name];
                currentInventory[targetGood.Index]
                    = new InvContent(value.quantity, targetGood.SpaceOccupied, targetGood.InitialCost);
                sizeCounter += value.quantity * targetGood.SpaceOccupied;
            }

            // Assigning rest of goods prices and statistics
            for (var index = 0; index < currentInventory.Length; index++)
            {
                currentStatistic[index] = new InvStats(goodArray[index].InitialCost);

                var targetInv = currentInventory[index];
                if (targetInv.RecordedPrice > 0.1)
                    continue;
                targetInv.RecordedPrice = goodArray[index].InitialCost;
                currentInventory[index] = targetInv;
            }

            startingUsedSpace.Add(jsonLogic.name, sizeCounter);
            startingInventories.Add(jsonLogic.name, currentInventory);
            startingStatistics.Add(jsonLogic.name, currentStatistic);

            using (var possibleDeltas = new NativeList<PossibleDelta>(Allocator.Temp))
            using (var deltas = new NativeList<DeltaValue>(Allocator.Temp))
            {
                foreach (var possibilities in jsonLogic.possibleDeltas)
                {
                    var consumeStart = deltas.Length;
                    foreach (var value in possibilities.consumes)
                        deltas.Add(new DeltaValue(goods[value.name].Index, value.quantity, value.possibility));

                    var produceStart = deltas.Length;
                    foreach (var value in possibilities.produces)
                        deltas.Add(new DeltaValue(goods[value.name].Index, value.quantity, value.possibility));

                    possibleDeltas.Add(new int3(consumeStart, produceStart, deltas.Length));
                }

                EntityManager.AddBuffer<PossibleDelta>(currentLogic).AddRange(possibleDeltas);
                EntityManager.AddBuffer<DeltaValue>(currentLogic).AddRange(deltas);
            }

            logic.Add(jsonLogic.name, currentLogic);
        }

        // DEBUG
        var counter = 0;

        CreateAgent("Farm", 100);
        CreateAgent("Farm", 100);
        CreateAgent("Farm", 100);
        CreateAgent("Farm", 100);
        CreateAgent("Farm", 100);
        CreateAgent("Farm", 100);

        CreateAgent("Mine", 100);
        CreateAgent("Ore_Refinery", 100);
        CreateAgent("Sawmill", 100);
        CreateAgent("Sawmill", 100);
        CreateAgent("Smithy", 100);
        CreateAgent("Smithy", 100);
        CreateAgent("Peasant", 50);
        CreateAgent("Peasant", 50);
        CreateAgent("Peasant", 50);
        CreateAgent("Peasant", 50);
        CreateAgent("Peasant", 50);

        foreach (var startingInventory in startingInventories)
            startingInventory.Value.Dispose();
        foreach (var startingStatistic in startingStatistics)
            startingStatistic.Value.Dispose();

        void CreateAgent(string factoryType, int wealth)
        {
            var currentAgent = EntityManager.CreateEntity(typeof(Agent), typeof(Wallet));
            EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic[factoryType]));
            EntityManager.SetComponentData(currentAgent, new Wallet(wealth));
            EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories[factoryType]);
            EntityManager.AddBuffer<InvStats>(currentAgent).AddRange(startingStatistics[factoryType]);
        }
    }

    protected override void OnUpdate()
    {
        Enabled = false;
    }
}