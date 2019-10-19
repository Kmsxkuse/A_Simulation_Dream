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
    public static List<string> GoodNames, LogicNames;

    protected override void OnCreate()
    {
        var goods = new Dictionary<string, Good>();
        GoodNames = new List<string>();
        LogicNames = new List<string>();

        foreach (var goodPath in Directory.EnumerateFiles(
            Path.Combine(Application.streamingAssetsPath, "Goods"), "*.json"))
        {
            var jsonGood = JsonGood.CreateFromJson(goodPath);
            goods.Add(jsonGood.name, new Good(goods.Count, jsonGood));
            GoodNames.Add(jsonGood.name);
        }

        // Adding simply money. Will always be the last value.
        goods.Add("Money", new Good(goods.Count, 1));

        var adjustedGoodsCount = goods.Count - 1;

        var goodArray = goods.Values.ToList();
        var logicGoodsCounter = new int[adjustedGoodsCount];
        GoodsMostLogic = new Entity[adjustedGoodsCount];

        var logic = new Dictionary<string, Entity>();
        var startingInventories = new Dictionary<string, NativeArray<InvContent>>();

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

                var idealQuantity = new NativeArray<IdealQuantity>(adjustedGoodsCount, Allocator.Temp);
                foreach (var value in jsonLogic.idealQuantity)
                    idealQuantity[goods[value.name].Index] = value.quantity;
                EntityManager.AddBuffer<IdealQuantity>(currentLogic).AddRange(idealQuantity);
                idealQuantity.Dispose();

                var currentInventory = new NativeArray<InvContent>(adjustedGoodsCount, Allocator.Temp);

                // Assigning starting good inventory
                foreach (var value in jsonLogic.startQuantity)
                {
                    var targetGood = goods[value.name];
                    currentInventory[targetGood.Index] = new InvContent(value.quantity, targetGood.InitialCost);
                }

                // Assigning rest of goods prices
                for (var index = 0; index < currentInventory.Length; index++)
                {
                    var targetInv = currentInventory[index];
                    if (targetInv.RecordedPrice > 0.1)
                        continue;
                    targetInv.RecordedPrice = goodArray[index].InitialCost;
                    currentInventory[index] = targetInv;
                }

                startingInventories.Add(jsonLogic.name, currentInventory);
                // Starting values
                EntityManager.AddBuffer<InvContent>(currentLogic).AddRange(currentInventory);

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
                LogicNames.Add(jsonLogic.name);
            }
        }

        // Subtract 1 for money being the last good.
        History.AssignLogs(adjustedGoodsCount, logic.Count);

        // DEBUG

        // GOAL: 605 PER!
        const int current = 1000;
        for (var counter = 0; counter < current; counter++)
            CreateAgent("Farm", 20);

        for (var counter = 0; counter < current; counter++)
            CreateAgent("Mine", 50);

        for (var counter = 0; counter < current; counter++)
            CreateAgent("Ore_Refinery", 50);

        for (var counter = 0; counter < current; counter++)
            CreateAgent("Sawmill", 50);

        for (var counter = 0; counter < current; counter++)
            CreateAgent("Smithy", 100);

        for (var counter = 0; counter < current; counter++)
            CreateAgent("Peasant", 50);

        foreach (var startingInventory in startingInventories)
            startingInventory.Value.Dispose();
        cofAndLg.Dispose();

        void CreateAgent(string factoryType, int wealth)
        {
            var currentAgent = EntityManager.CreateEntity(typeof(Agent), typeof(Wallet));
            EntityManager.SetComponentData(currentAgent, new Agent(logic[factoryType]));
            EntityManager.SetComponentData(currentAgent, new Wallet(wealth));
            EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories[factoryType]);
        }
    }

    protected override void OnUpdate()
    {
        Enabled = false;
    }
}