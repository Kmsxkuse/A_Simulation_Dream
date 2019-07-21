﻿using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

// ReSharper disable ForCanBeConvertedToForeach

public class MarketSystem : JobComponentSystem
{
    private EntityArchetype _agentArch;

    private NativeArray<float> _askHistory, _bidHistory, _tradeHistory, _priceHistory, _profitsHistory, _ratioHistory;
    private NativeQueue<Entity> _bankrupt;
    private BufferFromEntity<CostOfLivingAndLimitGood> _costOfLivingAndLimitGood;
    private NativeMultiHashMap<Entity, float> _deltaMoney, _profitsByLogic;
    private BufferFromEntity<DeltaValue> _deltaValues;

    private EntityCommandBuffer _ecb;

    private int _frameCount, _goodsCount;

    private BufferFromEntity<IdealQuantity> _idealQuantity;
    private NativeArray<Entity> _logicEntities, _goodsMostLogic;

    private NativeMultiHashMap<Entity, float3>
        _observedGoodHistories, _revisionism; // x: good index. y: cost. z: frame recorded

    private BufferFromEntity<PossibleDelta> _possibleDeltas;
    private NativeMultiHashMap<int, Offer> _tradeAsks, _tradeBids;

    protected override JobHandle OnUpdate(JobHandle inputDependencies)
    {
        _frameCount = Time.frameCount;

        Debug.Log(_frameCount);
        Debug.Log($"History: {_observedGoodHistories.Length}.");

        if (_frameCount > 500 || _observedGoodHistories.Length == 0 && _frameCount > 1)
        {
            // Dump info
            for (var index = 0; index < _priceHistory.Length; index++)
                Debug.Log($"G: {index}. " + _priceHistory[index]);

            Debug.Break();
        }

        var targetInvContent = GetBufferFromEntity<InvContent>();
        var targetInvStats = GetBufferFromEntity<InvStats>();
        var currentRng = new Random((uint) UnityEngine.Random.Range(1, 100000));

        _ecb = new EntityCommandBuffer(Allocator.TempJob);
        _costOfLivingAndLimitGood = GetBufferFromEntity<CostOfLivingAndLimitGood>(true);
        _possibleDeltas = GetBufferFromEntity<PossibleDelta>(true);
        _deltaValues = GetBufferFromEntity<DeltaValue>(true);
        _idealQuantity = GetBufferFromEntity<IdealQuantity>(true);

        var calcHistoryJob = new CalculateHistories
        {
            goodsCount = _goodsCount,
            history = _observedGoodHistories,
            revisionist = _revisionism.ToConcurrent(),
            inventoryStatistics = targetInvStats,
            currentFrameCount = _frameCount
        }.Schedule(this, inputDependencies);

        var oldHistoryJob = new BurnOldHistory
        {
            oldHistory = _observedGoodHistories
        }.Schedule(calcHistoryJob);

        var marketJobs = new ProcessAgentLogic
        {
            costOfLivingAndLimitGoods = _costOfLivingAndLimitGood,
            possibleDeltas = _possibleDeltas,
            deltaValues = _deltaValues,
            rng = currentRng,
            inventoryContents = targetInvContent
        }.Schedule(this, inputDependencies);

        marketJobs = new GenerateOffers
        {
            idealQuantities = _idealQuantity,
            inventoryContents = targetInvContent,
            inventoryStatistics = targetInvStats,
            tradeAsks = _tradeAsks.ToConcurrent(),
            tradeBids = _tradeBids.ToConcurrent()
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, calcHistoryJob));

        marketJobs = new ResolveOffers
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            currentFrame = _frameCount,
            inventoryContents = targetInvContent,
            rng = currentRng,
            goodHistory = _revisionism.ToConcurrent(),
            deltaMoney = _deltaMoney.ToConcurrent(),

            AskHistory = _askHistory,
            BidHistory = _bidHistory,
            TradeHistory = _tradeHistory,
            PriceHistory = _priceHistory
        }.Schedule(_goodsCount, 1, marketJobs);

        var completeChanges = new RewriteHistory
        {
            history = _observedGoodHistories.ToConcurrent(),
            revisionism = _revisionism
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, oldHistoryJob));

        var calculateRatio = new CalculateAskBidRatio
        {
            askHistory = _askHistory,
            bidHistory = _bidHistory,
            ratioHistory = _ratioHistory
        }.Schedule(_goodsCount, 1, marketJobs);

        marketJobs = new ProcessMoneyChanges
        {
            deltaMoney = _deltaMoney,
            bankrupt = _bankrupt.ToConcurrent(),
            profitsByLogic = _profitsByLogic.ToConcurrent()
        }.Schedule(this, marketJobs);

        marketJobs = new CollapseProfitsByLogic
        {
            profitsByLogic = _profitsByLogic,
            profitHistory = _profitsHistory
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, completeChanges));

        marketJobs = new ReplaceBankruptcies
        {
            profitHistory = _profitsHistory,
            ratioHistory = _ratioHistory,
            logicEntities = _logicEntities,
            goodsMostLogic = _goodsMostLogic,
            startingInv = targetInvContent,
            startingStats = targetInvStats,
            agentArch = _agentArch,
            bankrupt = _bankrupt,
            ecb = _ecb
        }.Schedule(JobHandle.CombineDependencies(marketJobs, calculateRatio));

        var resetMhm = new ResetMultiHashMaps
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            deltaMoney = _deltaMoney,
            revisionism = _revisionism,
            profitsByLogic = _profitsByLogic
        }.Schedule(marketJobs);

        marketJobs.Complete();
        _ecb.Playback(EntityManager);
        _ecb.Dispose();

        return resetMhm;
    }

    protected override void OnCreate()
    {
        _observedGoodHistories = new NativeMultiHashMap<Entity, float3>(1000, Allocator.Persistent);
        _revisionism = new NativeMultiHashMap<Entity, float3>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _tradeAsks = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 2, Allocator.Persistent);
        _tradeBids = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 2, Allocator.Persistent);
        _deltaMoney = new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _profitsByLogic =
            new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _bankrupt = new NativeQueue<Entity>(Allocator.Persistent);

        _goodsCount = History.GoodsCount;
        _agentArch = EntityManager.CreateArchetype(typeof(Agent), typeof(Wallet));

        _askHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        _bidHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        _tradeHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        _priceHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        _ratioHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);

        _logicEntities = EntityManager.CreateEntityQuery(typeof(Logic)).ToEntityArray(Allocator.Persistent);
        _profitsHistory = new NativeArray<float>(_logicEntities.Length, Allocator.Persistent);
        _goodsMostLogic = new NativeArray<Entity>(InitializeMarket.GoodsMostLogic, Allocator.Persistent);
        InitializeMarket.GoodsMostLogic = null;
    }

    protected override void OnStopRunning()
    {
        EntityManager.CompleteAllJobs();

        _observedGoodHistories.Dispose();
        _tradeAsks.Dispose();
        _tradeBids.Dispose();
        _deltaMoney.Dispose();
        _profitsByLogic.Dispose();
        _revisionism.Dispose();
        _bankrupt.Dispose();

        _askHistory.Dispose();
        _bidHistory.Dispose();
        _tradeHistory.Dispose();
        _priceHistory.Dispose();
        _profitsHistory.Dispose();
        _ratioHistory.Dispose();
        _logicEntities.Dispose();
        _goodsMostLogic.Dispose();
    }

    private struct Offer
    {
        public readonly Entity Source;
        public int Units;
        public readonly float Cost;

        public Offer(Entity source, int units, float cost)
        {
            Source = source;
            Units = units;
            Cost = cost;
        }

        public override string ToString()
        {
            return $"Source: {Source.Index}. Units: {Units}. Cost: {Cost}.";
        }
    }

    [BurstCompile]
    private struct ProcessAgentLogic : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<CostOfLivingAndLimitGood> costOfLivingAndLimitGoods;
        [ReadOnly] public BufferFromEntity<PossibleDelta> possibleDeltas;
        [ReadOnly] public BufferFromEntity<DeltaValue> deltaValues;

        [ReadOnly] public Random rng;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent agent)
        {
            var targetCostOfLivingAndLimitGoods = costOfLivingAndLimitGoods[agent.Logic].AsNativeArray();
            var targetInventory = inventoryContents[entity].AsNativeArray();

            // Checking for minimum living cost expenditure. Typically just 1 unit of food.
            // Also determining if Agent has too many produced goods.
            var skipping = false;
            for (var goodIndex = 0; goodIndex < targetCostOfLivingAndLimitGoods.Length; goodIndex++)
            {
                var currentChecking = targetCostOfLivingAndLimitGoods[goodIndex];
                var placeholder = targetInventory[goodIndex];
                placeholder.Quantity -= currentChecking.CostOfLiving;

                if (placeholder.Quantity < 0)
                {
                    skipping = true;
                    placeholder.Quantity = 0;
                }
                else if (placeholder.Quantity > currentChecking.LimitGoods)
                {
                    skipping = true;
                }

                targetInventory[goodIndex] = placeholder;
            }

            // Agents without minimum living cost or over limit goods to produce skip straight to market.
            if (skipping)
                return;

            var targetPossibleDeltas = possibleDeltas[agent.Logic].AsNativeArray();
            var targetDeltaValues = deltaValues[agent.Logic].AsNativeArray();

            var selectedDelta = new int3(-1);

            // Calculating proper delta/production line given current inventory
            for (var i = 0; i < targetPossibleDeltas.Length; i++)
            {
                var range = targetPossibleDeltas[i].Deltas;

                if (!MeetsDeltaRequirement(range.xy))
                    continue;

                selectedDelta = range;
                break;
            }

            bool MeetsDeltaRequirement(int2 range)
            {
                // Searching through consumptions to determine requirements
                for (var j = range.x; j < range.y; j++)
                    if (targetInventory[targetDeltaValues[j].Good].Quantity
                        <= (targetDeltaValues[j].Quantity > 0 ? targetDeltaValues[j].Quantity : 1))
                        return false;
                return true;
            }

            if (selectedDelta.x < 0)
                return;

            var variableConsumption = 0;

            for (var consume = selectedDelta.x; consume < selectedDelta.y; consume++)
            {
                var currentDeltaValue = targetDeltaValues[consume];
                var placeholder = targetInventory[currentDeltaValue.Good];

                // Must roll lower than probability to consume good
                if (rng.NextFloat(0, 1) > currentDeltaValue.Possibility)
                    continue;

                // Negative quantities indicate consumption up to that value.
                variableConsumption = currentDeltaValue.Quantity > 0
                    ? currentDeltaValue.Quantity
                    : math.min(placeholder.Quantity, currentDeltaValue.Quantity);

                placeholder.Quantity -= variableConsumption;

                targetInventory[currentDeltaValue.Good] = placeholder;
            }

            for (var produce = selectedDelta.y; produce < selectedDelta.z; produce++)
            {
                var currentDeltaValue = targetDeltaValues[produce];
                var placeholder = targetInventory[currentDeltaValue.Good];
                // Quantities less than zero for production multiplies consumption by value.
                placeholder.Quantity += currentDeltaValue.Quantity > 0
                    ? currentDeltaValue.Quantity
                    : variableConsumption * currentDeltaValue.Quantity;
                targetInventory[currentDeltaValue.Good] = placeholder;
            }
        }
    }

    [BurstCompile]
    private struct CalculateHistories : IJobForEachWithEntity<Wallet>
    {
        [ReadOnly] public int currentFrameCount;
        [ReadOnly] public int goodsCount;
        [ReadOnly] public NativeMultiHashMap<Entity, float3> history;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent revisionist;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvStats> inventoryStatistics;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int jobIndex, [ReadOnly] ref Wallet throwaway)
        {
            if (!history.TryGetFirstValue(entity, out var meanData, out var iterator))
                return;

            var targetInvStats = inventoryStatistics[entity].AsNativeArray();

            for (var goodsIndex = 0; goodsIndex < goodsCount; goodsIndex++)
            {
                var targetGood = targetInvStats[goodsIndex];
                targetGood.Transactions = 0;

                // Constrict the range by 10%
                targetGood.Maximum *= 0.9f;
                targetGood.Minimum *= 1.1f;

                targetInvStats[goodsIndex] = targetGood;
            }

            do
            {
                // Look back 5 turns for stats. Not larger because memory usage is exponential!
                if (currentFrameCount - meanData.z > 5)
                    continue;

                var targetGood = targetInvStats[(int) meanData.x];
                targetGood.Mean += (meanData.y - targetGood.Mean) / ++targetGood.Transactions;

                targetGood.Minimum = math.min(targetGood.Minimum, meanData.y);
                targetGood.Maximum = math.max(targetGood.Maximum, meanData.y);

                revisionist.Add(entity, meanData);
                targetInvStats[(int) meanData.x] = targetGood;
            } while (history.TryGetNextValue(out meanData, ref iterator));
        }
    }

    [BurstCompile]
    private struct GenerateOffers : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<IdealQuantity> idealQuantities;
        [ReadOnly] public BufferFromEntity<InvContent> inventoryContents;
        [ReadOnly] public BufferFromEntity<InvStats> inventoryStatistics;

        [WriteOnly] public NativeMultiHashMap<int, Offer>.Concurrent tradeAsks, tradeBids;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent agent)
        {
            // Determining if surplus or shortage
            var targetInventory = inventoryContents[entity].AsNativeArray();
            var targetStatistic = inventoryStatistics[entity].AsNativeArray();
            var targetIdeals = idealQuantities[agent.Logic].AsNativeArray();

            for (var good = 0; good < targetInventory.Length; good++)
            {
                var targetInvContent = targetInventory[good];
                if (targetInvContent.Quantity > targetIdeals[good])
                {
                    // Surplus
                    var reasonablePrice = targetInvContent.RecordedPrice * 1.02f;
                    //agent.NumProduct = targetGood.Quantity; // No clue what this does
                    // Intentional placing entire inventory out to market.
                    tradeAsks.Add(good, new Offer(entity, targetInvContent.Quantity, reasonablePrice));
                }
                else
                {
                    var shortage = targetIdeals[good] - targetInvContent.Quantity;
                    const int bidPrice = 0; // Why not get it for free?

                    var targetInvStat = targetStatistic[good];
                    // Higher if closer to minimum;
                    var preference = 1 - math.clamp((targetInvStat.Mean - targetInvStat.Minimum) /
                                                    (targetInvStat.Maximum - targetInvStat.Minimum), 0, 1);

                    var purchaseQuantity = (int) math.round(preference * shortage);

                    tradeBids.Add(good, new Offer(entity, purchaseQuantity, bidPrice));
                }
            }
        }
    }

    [BurstCompile]
    private struct ResolveOffers : IJobParallelFor
    {
        [ReadOnly] public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        [ReadOnly] public int currentFrame;
        [ReadOnly] public Random rng;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent goodHistory;
        [WriteOnly] public NativeMultiHashMap<Entity, float>.Concurrent deltaMoney;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> AskHistory, BidHistory, TradeHistory, PriceHistory;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute(int index)
        {
            var currentAsks = new NativeList<Offer>(Allocator.Temp);
            var currentBids = new NativeList<Offer>(Allocator.Temp);

            var numAsks = 0;
            var numBids = 0;

            if (tradeAsks.TryGetFirstValue(index, out var currentOffer, out var iterator))
            {
                do
                {
                    currentAsks.Add(currentOffer);
                    numAsks += currentOffer.Units;
                } while (tradeAsks.TryGetNextValue(out currentOffer, ref iterator));

                // Descending order (3, 2, 1). Normally left.CompareTo(right) for ascending order (1, 2, 3)
                currentAsks.AsArray().Sort(Comparer<Offer>.Create(
                    (left, right) => right.Cost.CompareTo(left.Cost)));
            }

            if (tradeBids.TryGetFirstValue(index, out currentOffer, out iterator))
            {
                do
                {
                    currentBids.Add(currentOffer);
                    numBids += currentOffer.Units;
                } while (tradeBids.TryGetNextValue(out currentOffer, ref iterator));

                // Randomizing bids
                var n = currentBids.Length;
                while (n-- > 1)
                {
                    var k = rng.NextInt(n + 1);
                    var placeholder = currentBids[k];
                    currentBids[k] = currentBids[n];
                    currentBids[n] = placeholder;
                }
            }

            AskHistory[index] = math.lerp(AskHistory[index], numAsks, 0.75f);
            BidHistory[index] = math.lerp(BidHistory[index], numBids, 0.75f);
            var numTraded = 0;
            var moneyTraded = 0f;

            while (currentBids.Length > 0 && currentAsks.Length > 0)
            {
                // Descending order
                var buyer = currentBids[currentBids.Length - 1];
                var seller = currentAsks[currentAsks.Length - 1];

                var quantityTraded = math.min(buyer.Units, seller.Units);
                var clearingPrice = seller.Cost;

                if (quantityTraded > 0)
                {
                    // Transferring goods
                    seller.Units -= quantityTraded;
                    buyer.Units -= quantityTraded;

                    // Recording history
                    numTraded += quantityTraded;
                    moneyTraded += clearingPrice * quantityTraded;

                    currentAsks[currentAsks.Length - 1] = seller;
                    currentBids[currentBids.Length - 1] = buyer;

                    var targetInv = inventoryContents[seller.Source].AsNativeArray();
                    var placeholder = targetInv[index];
                    placeholder.Quantity =
                        math.clamp(placeholder.Quantity - quantityTraded, 0, placeholder.Quantity);
                    placeholder.RecordedPrice = (placeholder.Quantity * placeholder.RecordedPrice + quantityTraded
                                                 * clearingPrice) / (placeholder.Quantity + quantityTraded);
                    targetInv[index] = placeholder;

                    targetInv = inventoryContents[buyer.Source].AsNativeArray();
                    placeholder = targetInv[index];
                    placeholder.RecordedPrice = clearingPrice;
                    placeholder.Quantity += quantityTraded;
                    targetInv[index] = placeholder;

                    deltaMoney.Add(seller.Source, clearingPrice * quantityTraded);
                    deltaMoney.Add(buyer.Source, -clearingPrice * quantityTraded);

                    var memory = new float3(index, clearingPrice, currentFrame);
                    //Debug.Log(memory);

                    goodHistory.Add(buyer.Source, memory);
                    goodHistory.Add(seller.Source, memory);
                }

                if (seller.Units <= 0)
                    currentAsks.RemoveAtSwapBack(currentAsks.Length - 1);

                if (buyer.Units <= 0)
                    currentBids.RemoveAtSwapBack(currentBids.Length - 1);
            }

            TradeHistory[index] = math.lerp(TradeHistory[index], numTraded, 0.75f);
            if (numTraded > 0)
                PriceHistory[index] = moneyTraded / numTraded;
        }
    }

    [BurstCompile]
    private struct ProcessMoneyChanges : IJobForEachWithEntity<Wallet, Agent>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float> deltaMoney;

        [WriteOnly] public NativeMultiHashMap<Entity, float>.Concurrent profitsByLogic;
        [WriteOnly] public NativeQueue<Entity>.Concurrent bankrupt;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Wallet wallet,
            [ReadOnly] ref Agent agent)
        {
            if (!deltaMoney.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            wallet.MoneyLastRound = wallet.Money;

            do
            {
                wallet.Money += transaction;
            } while (deltaMoney.TryGetNextValue(out transaction, ref iterator));

            wallet.Profit += wallet.Money - wallet.MoneyLastRound;
            profitsByLogic.Add(agent.Logic, wallet.Profit);

            if (wallet.Money > 0)
                return;

            // Bankrupt
            bankrupt.Enqueue(entity);
        }
    }

    [BurstCompile]
    private struct CalculateAskBidRatio : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> askHistory, bidHistory;

        [NativeDisableParallelForRestriction] public NativeArray<float> ratioHistory;

        public void Execute(int index)
        {
            var newRatio = 0.5f;
            if (askHistory[index] > 0.05f)
                newRatio = bidHistory[index] / askHistory[index];

            ratioHistory[index] = math.lerp(ratioHistory[index], newRatio, 0.75f);
        }
    }

    [BurstCompile]
    private struct ReplaceBankruptcies : IJob
    {
        [ReadOnly] public NativeArray<float> profitHistory, ratioHistory;
        [ReadOnly] public NativeArray<Entity> logicEntities, goodsMostLogic;
        [ReadOnly] public BufferFromEntity<InvContent> startingInv;
        [ReadOnly] public BufferFromEntity<InvStats> startingStats;

        [ReadOnly] public EntityArchetype agentArch;

        public NativeQueue<Entity> bankrupt;
        [WriteOnly] public EntityCommandBuffer ecb;

        public void Execute()
        {
            if (bankrupt.Count < 1)
                return;

            // Calculate highest ratio
            var maximum = -99999f;
            var targetLogic = Entity.Null;
            for (var goodIndex = 0; goodIndex < ratioHistory.Length; goodIndex++)
            {
                if (ratioHistory[goodIndex] < maximum)
                    continue;
                targetLogic = goodsMostLogic[goodIndex];
                maximum = ratioHistory[goodIndex];
            }

            // Agents will prioritize the good of all over profit. Unrealistic yes.
            if (maximum < 2.5f)
            {
                // Calculate most profitable
                maximum = -99999f;
                for (var logicIndex = 0; logicIndex < profitHistory.Length; logicIndex++)
                {
                    if (profitHistory[logicIndex] < maximum)
                        continue;
                    targetLogic = logicEntities[logicIndex];
                    maximum = profitHistory[logicIndex];
                }
            }

            var sC = startingInv[targetLogic].AsNativeArray();
            var sS = startingStats[targetLogic].AsNativeArray();

            while (bankrupt.TryDequeue(out var replaceEntity))
            {
                ecb.DestroyEntity(replaceEntity);

                replaceEntity = ecb.CreateEntity(agentArch);
                ecb.SetComponent(replaceEntity, new Agent(targetLogic));
                ecb.SetComponent(replaceEntity, new Wallet(100));
                ecb.AddBuffer<InvContent>(replaceEntity).AddRange(sC);
                ecb.AddBuffer<InvStats>(replaceEntity).AddRange(sS);
            }
        }
    }

    [BurstCompile]
    private struct CollapseProfitsByLogic : IJobForEachWithEntity<Logic>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float> profitsByLogic;

        [NativeDisableParallelForRestriction] public NativeArray<float> profitHistory;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Logic logic)
        {
            if (!profitsByLogic.TryGetFirstValue(entity, out var profit, out var iterator))
                return;

            do
            {
                // Get halfway point between old and new profits.
                profitHistory[logic.Index] = math.lerp(profit, profitHistory[logic.Index], 0.5f);
            } while (profitsByLogic.TryGetNextValue(out profit, ref iterator));
        }
    }

    [BurstCompile]
    private struct RewriteHistory : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float3> revisionism;
        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent history;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent throwaway)
        {
            if (!revisionism.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            do
            {
                history.Add(entity, transaction);
            } while (revisionism.TryGetNextValue(out transaction, ref iterator));
        }
    }

    [BurstCompile]
    private struct ResetMultiHashMaps : IJob
    {
        public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        public NativeMultiHashMap<Entity, float> deltaMoney, profitsByLogic;
        public NativeMultiHashMap<Entity, float3> revisionism;

        public void Execute()
        {
            tradeAsks.Clear();
            tradeBids.Clear();
            deltaMoney.Clear();
            profitsByLogic.Clear();
            revisionism.Clear();
        }
    }

    [BurstCompile]
    private struct BurnOldHistory : IJob
    {
        public NativeMultiHashMap<Entity, float3> oldHistory;

        public void Execute()
        {
            oldHistory.Clear();
        }
    }
}