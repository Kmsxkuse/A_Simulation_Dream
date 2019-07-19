using System.Collections.Generic;
using System.IO;
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
            var currentGood = EntityManager.CreateEntity(typeof(Good));
            var goodValue = new Good(goods.Count, jsonGood);
            EntityManager.SetComponentData(currentGood, goodValue);
            goods.Add(jsonGood.name, goodValue);
        }
        
        History.AssignLogs(goods.Count);
        foreach (var index in goods)
        {
            // Start history charts with 1 fake buy/sell bid
            History.Prices[index.Value.Index].Add(index.Value.Mean);
            History.Asks[index.Value.Index].Add(index.Value.Mean);
            History.Bids[index.Value.Index].Add(index.Value.Mean);
            History.Trades[index.Value.Index].Add(index.Value.Mean);
        }

        var logic = new Dictionary<string, Entity>();
        var startingInventories = new Dictionary<string, NativeArray<InvContent>>();
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

            var sizeCounter = 0;
            foreach (var value in jsonLogic.startQuantity)
            {
                var targetGood = goods[value.name];
                currentInventory[targetGood.Index]
                    = new InvContent(value.quantity, targetGood.Mean);
                sizeCounter += value.quantity * targetGood.SpaceOccupied;
            }
            startingUsedSpace.Add(jsonLogic.name, sizeCounter);
            startingInventories.Add(jsonLogic.name, currentInventory);

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
        var agentArch = EntityManager.CreateArchetype(typeof(Agent), typeof(Wallet), typeof(Inventory));
        var currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Farm"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(100));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Farm"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Farm"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Mine"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(100));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Mine"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Mine"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Ore_Refinery"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(100));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Ore_Refinery"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Ore_Refinery"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Sawmill"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(100));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Sawmill"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Sawmill"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Smithy"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(100));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Smithy"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Smithy"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Peasant"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(10));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Peasant"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Peasant"]);
        
        currentAgent = EntityManager.CreateEntity(agentArch);
        EntityManager.SetComponentData(currentAgent, new Agent(++counter, logic["Peasant"]));
        EntityManager.SetComponentData(currentAgent, new Wallet(10));
        EntityManager.SetComponentData(currentAgent, new Inventory(30, startingUsedSpace["Peasant"]));
        EntityManager.AddBuffer<InvContent>(currentAgent).AddRange(startingInventories["Peasant"]);

        foreach (var startingInventory in startingInventories)
            startingInventory.Value.Dispose();
    }

    protected override void OnUpdate()
    {
        Enabled = false;
    }
}