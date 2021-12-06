﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class Snapshot
    {

        public static readonly string queueName = "snapshot";
        public ClusterData customMetrics { get; }
        public List<NodeData> nodeMetrics { get; }

        // All these attributes represent cluster usage in % at this snapshot
        private float AverageClusterCpuUsage;
        private float AverageClusterRamUsage;
        private float AverageClusterDiskUsage;

        public Snapshot(ClusterData customMetrics,List<NodeData> nodeMetrics)
        {
            this.customMetrics = customMetrics;
            this.nodeMetrics = nodeMetrics;
        }



        public float CalculateAverageCapacity()
        {
            AverageClusterResourseUsage();
            float bottleneck = 0;
            bottleneck = Math.Max(AverageClusterCpuUsage, Math.Max(AverageClusterRamUsage, AverageClusterDiskUsage));
            return (float)Math.Floor((100 * customMetrics.Count) / bottleneck);
            
        }

        public static bool checkTime(double minTime, double dataTime)
        {
            double delta = Math.Abs(minTime - dataTime);
            if (delta < SFUtilities.interval) return true;
            return false;
        }

        /// <summary>
        /// Calculates the average % resourse usage of the cluster at this snapshot
        /// </summary>
        /// <returns>% usage</returns>
        public (float cpu,float ram,float disk) AverageClusterResourseUsage()
        {
            float cpuSum = 0;
            float ramSum = 0;
            float diskSum = 0;
            int cnt = nodeMetrics.Count;
            foreach(NodeData nodeMetric in nodeMetrics)
            {
                cpuSum += nodeMetric.hardware.Cpu;
                ramSum += (float) nodeMetric.hardware.PercentInUse;
                diskSum += nodeMetric.hardware.DiskPercentInUse;

            }
            AverageClusterCpuUsage = ((float)cpuSum) / cnt;
            AverageClusterRamUsage = ((float)ramSum) / cnt;
            AverageClusterDiskUsage = ((float)diskSum) / cnt;
            return (AverageClusterCpuUsage, AverageClusterRamUsage, AverageClusterDiskUsage);
        }

        public string ToStringAllData()
        {
            string res=customMetrics.ToString();
            foreach(var n in nodeMetrics)
            {
                res += n.ToString();
            }
            res += "\n************************************************************************************************";
            return res;
        }

        public override string ToString()
        {
            string res;
            res =
                "\n Average cluster capacity: " + this.CalculateAverageCapacity() +
                "\n Average resource usage: " + this.AverageClusterResourseUsage() +
                "\n";
            return res;
        }

        //public (int totalPrimaryCount,int totalReplicaCount,int totalInstanceCount, int totalCount) TupleGetNodeTotalCounts(string NodeName)
        //{
        //    int totalPrimaryCount = 0, totalReplicaCount = 0, totalInstanceCount = 0, totalCount = 0;
        //    foreach (var node in nodeMetrics)
        //    {
        //        if (node.nodeName != NodeName) continue;
        //        foreach(var process in node.processList)
        //        {
        //            totalPrimaryCount += process.primaryCount;
        //            totalReplicaCount += process.replicaCount;
        //            totalInstanceCount += process.instaceCount;
        //            totalCount += process.count;
        //        }
        //    }
        //    return (totalPrimaryCount, totalReplicaCount, totalInstanceCount, totalCount);
        //}
        //public Hardware GetNodeHardwareData(string NodeName)
        //{
        //    foreach (var node in nodeMetrics)
        //    {
        //        if (node.nodeName != NodeName) continue;
        //        return node.hardware;
        //    }
        //    return null;
        //}
        ///// <summary>
        ///// Returns a dctionary <Uri,ProcessData>
        ///// </summary>
        ///// <param name="NodeName"></param>
        ///// <returns></returns>
        //public Dictionary<Uri,ProcessData> GetNodeProcessesHardwareData(string NodeName)
        //{   
        //    foreach (var node in nodeMetrics)
        //    {
        //        if (node.nodeName != NodeName) continue;
        //        Dictionary<Uri, ProcessData> dic=new Dictionary<Uri, ProcessData>();
        //        foreach(var process in node.processList)
        //        {
        //            Uri uri = process.serviceUris[0];
        //            dic.Add(uri, process);
        //        }
        //        return dic;
        //    }
        //    return null;
        //}

        public static NodeData AverageNodeData(string NodeName,List<Snapshot> snapshots)
        {
            List<NodeData> list = new List<NodeData>();
            
            foreach(Snapshot s in snapshots)
            {
                foreach(var nodeData in s.nodeMetrics)
                {
                    if (nodeData.nodeName == NodeName)
                    {
                        list.Add(nodeData);
                    }
                }
            }
            return NodeData.AverageNodeData(list);
        }
    }
}
