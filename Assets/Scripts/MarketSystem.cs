using System;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Random = Unity.Mathematics.Random;

// ReSharper disable ForCanBeConvertedToForeach

// Handled in History
[DisableAutoCreation]
public class MarketSystem : JobComponentSystem
{
    private EntityArchetype _agentArch;
    private NativeQueue<BankruptcyInfo> _bankrupt;
    private BufferFromEntity<CostOfLivingAndLimitGood> _costOfLivingAndLimitGood;
    private NativeMultiHashMap<Entity, float> _deltaMoney, _profitsByLogic;
    private BufferFromEntity<DeltaValue> _deltaValues;

    private EndSimulationEntityCommandBufferSystem _ecbBarrier;

    private int _frameCount, _goodsCount;

    private BufferFromEntity<IdealQuantity> _idealQuantity;
    private NativeArray<Entity> _logicEntities, _goodsMostLogic;

    private NativeMultiHashMap<Entity, float3>
        _observedGoodHistories, _revisionism; // x: good index. y: cost. z: frame recorded

    private BufferFromEntity<PossibleDelta> _possibleDeltas;
    private NativeMultiHashMap<int, Offer> _tradeAsks, _tradeBids;

    public NativeArray<float> askHistory,
        bidHistory,
        tradeHistory,
        priceHistory,
        profitsHistory,
        ratioHistory,
        fieldHistory;

    public void ClearLog()
    {
        var assembly = Assembly.GetAssembly(typeof(Editor));
        var type = assembly.GetType("UnityEditor.LogEntries");
        var method = type.GetMethod("Clear");
        method?.Invoke(new object(), null);
    }

    protected override JobHandle OnUpdate(JobHandle inputDependencies)
    {
        _frameCount++;

        if (_frameCount > 10000 || _observedGoodHistories.Length == 0 && _frameCount > 1)
        {
            Debug.Log(_frameCount);
            Debug.Break();
        }

        var targetInvContent = GetBufferFromEntity<InvContent>();
        var targetInvStats = GetBufferFromEntity<InvStats>();
        var currentRng = new Random((uint) UnityEngine.Random.Range(1, 100000));

        _costOfLivingAndLimitGood = GetBufferFromEntity<CostOfLivingAndLimitGood>(true);
        _possibleDeltas = GetBufferFromEntity<PossibleDelta>(true);
        _deltaValues = GetBufferFromEntity<DeltaValue>(true);
        _idealQuantity = GetBufferFromEntity<IdealQuantity>(true);

        var calcHistoryJob = new CalculateHistories
        {
            history = _observedGoodHistories,
            revisionist = _revisionism.ToConcurrent(),
            inventoryStatistics = targetInvStats,
            currentFrameCount = _frameCount
        }.Schedule(this, inputDependencies);

        var oldHistoryJob = new BurnOldHistory
        {
            oldHistory = _observedGoodHistories
        }.Schedule(calcHistoryJob);

        var marketJobs = new SubtractCostsOfLiving
        {
            costOfLivingAndLimitGoods = _costOfLivingAndLimitGood,
            inventoryContents = GetBufferFromEntity<InvContent>()
        }.Schedule(this, inputDependencies);

        marketJobs = new ProcessAgentLogic
        {
            possibleDeltas = _possibleDeltas,
            deltaValues = _deltaValues,
            rng = currentRng,
            inventoryContents = targetInvContent
        }.Schedule(this, marketJobs);

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

            AskHistory = askHistory,
            BidHistory = bidHistory,
            TradeHistory = tradeHistory,
            PriceHistory = priceHistory
        }.Schedule(_goodsCount, 1, marketJobs);

        var completeChanges = new RewriteHistory
        {
            history = _observedGoodHistories.ToConcurrent(),
            revisionism = _revisionism
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, oldHistoryJob));

        var calculateRatio = new CalculateAskBidRatio
        {
            askHistory = askHistory,
            bidHistory = bidHistory,
            ratioHistory = ratioHistory
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
            profitHistory = profitsHistory,
            fieldHistory = fieldHistory
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, completeChanges));

        marketJobs = new ReplaceBankruptcies
        {
            profitHistory = profitsHistory,
            ratioHistory = ratioHistory,
            fieldHistory = fieldHistory,
            logicEntities = _logicEntities,
            goodsMostLogic = _goodsMostLogic,
            startingInv = targetInvContent,
            startingStats = targetInvStats,
            logicData = GetComponentDataFromEntity<Logic>(true),
            agentArch = _agentArch,
            bankrupt = _bankrupt,
            ecb = _ecbBarrier.CreateCommandBuffer()
        }.Schedule(JobHandle.CombineDependencies(marketJobs, calculateRatio));

        _ecbBarrier.AddJobHandleForProducer(marketJobs);

        marketJobs = new ResetMultiHashMaps
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            deltaMoney = _deltaMoney,
            revisionism = _revisionism,
            profitsByLogic = _profitsByLogic
        }.Schedule(marketJobs);

        return marketJobs;
    }

    protected override void OnCreate()
    {
        _frameCount = 0;
        // End simulation is at end of Update(). Not end of frame.
        _ecbBarrier = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();

        _observedGoodHistories = new NativeMultiHashMap<Entity, float3>(10000, Allocator.Persistent);
        _revisionism = new NativeMultiHashMap<Entity, float3>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _tradeAsks = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 2, Allocator.Persistent);
        _tradeBids = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 2, Allocator.Persistent);
        _deltaMoney = new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _profitsByLogic =
            new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _bankrupt = new NativeQueue<BankruptcyInfo>(Allocator.Persistent);

        _goodsCount = History.GoodsCount;
        _agentArch = EntityManager.CreateArchetype(typeof(AgTag), typeof(Agent), typeof(Wallet));

        askHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        bidHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        tradeHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        priceHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);
        ratioHistory = new NativeArray<float>(_goodsCount, Allocator.Persistent);

        _logicEntities = EntityManager.CreateEntityQuery(typeof(Logic)).ToEntityArray(Allocator.Persistent);
        profitsHistory = new NativeArray<float>(_logicEntities.Length, Allocator.Persistent);
        fieldHistory = new NativeArray<float>(_logicEntities.Length, Allocator.Persistent);
        new GatherAgentsPerEntityCount
        {
            fieldHistory = fieldHistory,
            logicData = GetComponentDataFromEntity<Logic>(true)
        }.ScheduleSingle(this).Complete(); // Single threaded intentionally

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

        askHistory.Dispose();
        bidHistory.Dispose();
        tradeHistory.Dispose();
        priceHistory.Dispose();
        profitsHistory.Dispose();
        ratioHistory.Dispose();
        fieldHistory.Dispose();
        _logicEntities.Dispose();
        _goodsMostLogic.Dispose();
    }

    private struct Offer : IComparable<Offer>
    {
        public readonly Entity Source;
        public float Units;
        public readonly float Cost;

        public Offer(Entity source, float units, float cost)
        {
            Source = source;
            Units = units;
            Cost = cost;
        }

        public override string ToString()
        {
            return $"Source: {Source.Index}. Units: {Units}. Cost: {Cost}.";
        }

        public int CompareTo(Offer other)
        {
            return -Cost.CompareTo(other.Cost);
        }

        public static bool operator <(Offer left, Offer right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator >(Offer left, Offer right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator <=(Offer left, Offer right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >=(Offer left, Offer right)
        {
            return left.CompareTo(right) >= 0;
        }
    }

    [BurstCompile]
    private struct GatherAgentsPerEntityCount : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public ComponentDataFromEntity<Logic> logicData;

        public NativeArray<float> fieldHistory;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent agent)
        {
            fieldHistory[logicData[agent.Logic].Index]++;
        }
    }

    [BurstCompile]
    private struct SubtractCostsOfLiving : IJobForEachWithEntity<Agent, Wallet>
    {
        [ReadOnly] public BufferFromEntity<CostOfLivingAndLimitGood> costOfLivingAndLimitGoods;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Agent agent, ref Wallet wallet)
        {
            var targetCostOfLivingAndLimitGoods = costOfLivingAndLimitGoods[agent.Logic].AsNativeArray();
            var targetInventory = inventoryContents[entity].AsNativeArray();

            // Checking for minimum living cost expenditure. Typically just 1 unit of food.
            // Also determining if Agent has too many produced goods.
            var goodsLength = targetCostOfLivingAndLimitGoods.Length;
            for (var goodIndex = 0; goodIndex < goodsLength - 1; goodIndex++)
            {
                var currentChecking = targetCostOfLivingAndLimitGoods[goodIndex];
                var placeholder = targetInventory[goodIndex];
                placeholder.Quantity -= currentChecking.CostOfLiving;

                if (placeholder.Quantity < 0)
                {
                    agent.Skipping = true;
                    placeholder.Quantity = 0;
                }
                else if (placeholder.Quantity > currentChecking.LimitGoods)
                {
                    agent.Skipping = true;
                }

                targetInventory[goodIndex] = placeholder;
            }

            // Special direct money upkeep
            if (targetCostOfLivingAndLimitGoods[goodsLength - 1].CostOfLiving > 0)
                wallet.Money -= targetCostOfLivingAndLimitGoods[goodsLength - 1].CostOfLiving;
        }
    }

    [BurstCompile]
    private struct ProcessAgentLogic : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<PossibleDelta> possibleDeltas;
        [ReadOnly] public BufferFromEntity<DeltaValue> deltaValues;
        [ReadOnly] public Random rng;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Agent agent)
        {
            if (agent.Skipping)
            {
                // Agents without minimum living cost or over limit goods to produce skip straight to market.
                agent.Skipping = false;
                return;
            }

            var targetInventory = inventoryContents[entity].AsNativeArray();
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

                agent.AverageRequirement = math.lerp(agent.AverageRequirement, i, 0.5f);
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

            var variableConsumption = 0f;

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
                    : math.min(placeholder.Quantity, math.abs(currentDeltaValue.Quantity));

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
                    : math.abs(variableConsumption * currentDeltaValue.Quantity);
                targetInventory[currentDeltaValue.Good] = placeholder;
            }
        }
    }

    [BurstCompile]
    private struct CalculateHistories : IJobForEachWithEntity<AgTag>
    {
        [ReadOnly] public int currentFrameCount;
        [ReadOnly] public NativeMultiHashMap<Entity, float3> history;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent revisionist;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvStats> inventoryStatistics;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int jobIndex, [ReadOnly] ref AgTag throwaway)
        {
            if (!history.TryGetFirstValue(entity, out var meanData, out var iterator))
                return;

            var targetInvStats = inventoryStatistics[entity].AsNativeArray();

            for (var goodsIndex = 0; goodsIndex < targetInvStats.Length; goodsIndex++)
            {
                var targetGood = targetInvStats[goodsIndex];
                targetGood.Transactions = 0;

                // Constrict the range by 10%
                targetGood.Maximum = math.clamp(targetGood.Maximum * 0.9f, targetGood.Mean * 1.1f, 10);
                targetGood.Minimum = math.clamp(targetGood.Minimum * 1.1f, 0.01f, targetGood.Mean * 0.95f);

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
                    // Surplus. 2% markup
                    var reasonablePrice = math.clamp(targetInvContent.RecordedPrice * 1.02f, 0.005f, 5f);
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

                    var purchaseQuantity = preference * shortage;

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

            var numAsks = 0f;
            var numBids = 0f;

            if (tradeAsks.TryGetFirstValue(index, out var currentOffer, out var iterator))
            {
                do
                {
                    currentAsks.Add(currentOffer);
                    numAsks += currentOffer.Units;
                } while (tradeAsks.TryGetNextValue(out currentOffer, ref iterator));

                // Descending order (3, 2, 1).
                currentAsks.AsArray().Sort();
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
            var numTraded = 0f;
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
                    placeholder.Quantity -= quantityTraded;
                    // TODO: Find out why this is causing skyrocketing prices.
                    placeholder.RecordedPrice = math.lerp(placeholder.RecordedPrice, clearingPrice,
                        quantityTraded / (quantityTraded + placeholder.Quantity));
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

            for (var unsold = 0; unsold < currentAsks.Length; unsold++)
            {
                var seller = currentAsks[unsold];
                var targetInv = inventoryContents[seller.Source].AsNativeArray();
                var placeholder = targetInv[index];
                placeholder.RecordedPrice = math.clamp(
                    placeholder.RecordedPrice * (1 - 0.01f * math.sqrt(seller.Units)),
                    0.005f, 10f);
                targetInv[index] = placeholder;
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
        [WriteOnly] public NativeQueue<BankruptcyInfo>.Concurrent bankrupt;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Wallet wallet,
            [ReadOnly] ref Agent agent)
        {
            if (!deltaMoney.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            wallet.MoneyLastRound = wallet.Money;

            do
            {
                wallet.Money += transaction; // Income tax.
            } while (deltaMoney.TryGetNextValue(out transaction, ref iterator));

            wallet.Money *= 1 - 0.005f * math.sign(wallet.Profit);

            wallet.Profit += wallet.Money - wallet.MoneyLastRound;
            profitsByLogic.Add(agent.Logic, wallet.Profit);

            if (wallet.Money > 0)
                return;

            // Bankrupt
            bankrupt.Enqueue(new BankruptcyInfo(entity, agent.Logic));
        }
    }

    [BurstCompile]
    private struct CalculateAskBidRatio : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> askHistory, bidHistory;

        [NativeDisableParallelForRestriction] public NativeArray<float> ratioHistory;

        public void Execute(int index)
        {
            var newRatio = 2f;
            if (askHistory[index] > 0.05f)
                newRatio = bidHistory[index] / askHistory[index];

            ratioHistory[index] = math.lerp(ratioHistory[index], newRatio, 0.5f);
        }
    }

    //[BurstCompile]
    private struct ReplaceBankruptcies : IJob
    {
        [ReadOnly] public NativeArray<float> profitHistory, ratioHistory;
        [ReadOnly] public NativeArray<Entity> logicEntities, goodsMostLogic;
        [ReadOnly] public BufferFromEntity<InvContent> startingInv;
        [ReadOnly] public BufferFromEntity<InvStats> startingStats;
        [ReadOnly] public ComponentDataFromEntity<Logic> logicData;

        [ReadOnly] public EntityArchetype agentArch;

        public NativeQueue<BankruptcyInfo> bankrupt;
        public NativeArray<float> fieldHistory;
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

            // Agents will prioritize the good of all over profit. Unrealistic, yes, but necessary.
            if (maximum < 1.5f)
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

            fieldHistory[targetLogic.Index] += bankrupt.Count;

            var sC = startingInv[targetLogic].AsNativeArray();
            var sS = startingStats[targetLogic].AsNativeArray();

            while (bankrupt.TryDequeue(out var replaceEntity))
            {
                fieldHistory[logicData[replaceEntity.Logic].Index]--;
                ecb.DestroyEntity(replaceEntity.Agent);

                var newAgent = ecb.CreateEntity(agentArch);
                ecb.SetComponent(newAgent, new Agent(targetLogic));
                ecb.SetComponent(newAgent, new Wallet(30));
                ecb.AddBuffer<InvContent>(newAgent).AddRange(sC);
                ecb.AddBuffer<InvStats>(newAgent).AddRange(sS);
            }
        }
    }

    [BurstCompile]
    private struct CollapseProfitsByLogic : IJobForEachWithEntity<Logic>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float> profitsByLogic;
        [ReadOnly] public NativeArray<float> fieldHistory;

        [NativeDisableParallelForRestriction] public NativeArray<float> profitHistory;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Logic logic)
        {
            if (!profitsByLogic.TryGetFirstValue(entity, out var profit, out var iterator))
                return;

            do
            {
                // Find rough average of profits
                profitHistory[logic.Index] = math.lerp(profitHistory[logic.Index], profit,
                    1 / fieldHistory[logic.Index]);
            } while (profitsByLogic.TryGetNextValue(out profit, ref iterator));
        }
    }

    [BurstCompile]
    private struct RewriteHistory : IJobForEachWithEntity<AgTag>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float3> revisionism;
        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent history;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref AgTag throwaway)
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