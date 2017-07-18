﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor.Build.Utilities;
using UnityEditor.Experimental.Build;
using UnityEditor.Experimental.Build.AssetBundle;
using UnityEngine;

namespace UnityEditor.Build.AssetBundle.DataConverters
{
    public class CommandSetProcessor : IDataConverter<BuildInput, BuildDependencyInformation, BuildCommandSet>
    {
        private const string kUnityDefaultResourcePath = "library/unity default resources";

        public uint Version { get { return 1; } }

        private Hash128 CalculateInputHash(BuildInput input, BuildDependencyInformation buildInfo, bool useCache)
        {
            if (!useCache)
                return new Hash128();

            return HashingMethods.CalculateMD5Hash(Version, input, buildInfo.assetLoadInfo, buildInfo.assetToBundle, buildInfo.sceneUsageTags);
        }

        public bool Convert(BuildInput input, BuildDependencyInformation buildInfo, out BuildCommandSet output, bool useCache = true)
        {
            Hash128 hash = CalculateInputHash(input, buildInfo, useCache);
            if (useCache && BuildCache.TryLoadCachedResults(hash, out output))
                return true;
            
            var commands = new List<BuildCommandSet.Command>();

            foreach (var bundle in input.definitions)
            {
                var command = new BuildCommandSet.Command();
                var explicitAssets = new List<BuildCommandSet.AssetLoadInfo>();
                var assetBundleObjects = new List<BuildCommandSet.SerializationInfo>();
                var addedObjects = new HashSet<ObjectIdentifier>();
                var dependencies = new HashSet<string>();

                foreach (var asset in bundle.explicitAssets)
                {
                    var assetInfo = buildInfo.assetLoadInfo[asset.asset];
                    explicitAssets.Add(assetInfo);

                    foreach (var includedObject in assetInfo.includedObjects)
                    {
                        addedObjects.Add(includedObject);
                        assetBundleObjects.Add(new BuildCommandSet.SerializationInfo
                        {
                            serializationObject = includedObject,
                            serializationIndex = SerializationIndexFromObjectIdentifier(includedObject)
                        });
                    }

                    foreach (var referencedObject in assetInfo.referencedObjects)
                    {
                        if (addedObjects.Contains(referencedObject))
                            continue;

                        if (referencedObject.filePath == kUnityDefaultResourcePath)
                            continue;

                        string dependency;
                        if (buildInfo.assetToBundle.TryGetValue(referencedObject.guid, out dependency))
                        {
                            addedObjects.Add(referencedObject);
                            dependencies.Add(dependency);
                            continue;
                        }

                        addedObjects.Add(referencedObject);
                        assetBundleObjects.Add(new BuildCommandSet.SerializationInfo
                        {
                            serializationObject = referencedObject,
                            serializationIndex = SerializationIndexFromObjectIdentifier(referencedObject)
                        });
                    }

                    BuildUsageTagGlobal globalUsage;
                    if (buildInfo.sceneUsageTags.TryGetValue(asset.asset, out globalUsage))
                    {
                        command.sceneBundle = true;
                        command.globalUsage |= globalUsage;
                    }
                }

                assetBundleObjects.Sort(Compare);

                command.assetBundleName = bundle.assetBundleName;
                command.explicitAssets = explicitAssets.ToArray();
                command.assetBundleDependencies = dependencies.OrderBy(x => x).ToArray();  // I hate Linq, but this is too easy
                command.assetBundleObjects = assetBundleObjects.ToArray();
                commands.Add(command);
            }

            output = new BuildCommandSet();
            output.commands = commands.ToArray();

            if (useCache && !BuildCache.SaveCachedResults(hash, output))
                BuildLogger.LogWarning("Unable to cache CommandSetProcessor results.");
            return true;
        }

        public static long SerializationIndexFromObjectIdentifier(ObjectIdentifier objectID)
        {
            byte[] bytes;
            var md4 = MD4.Create();
            if (objectID.fileType == FileType.MetaAssetType || objectID.fileType == FileType.SerializedAssetType)
            {
                // TODO: Variant info
                // NOTE: ToString() required as unity5 used the guid as a string to hash
                bytes = Encoding.ASCII.GetBytes(objectID.guid.ToString());
                md4.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                bytes = BitConverter.GetBytes((int)objectID.fileType);
                md4.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            // Or path
            else
            {
                bytes = Encoding.ASCII.GetBytes(objectID.filePath);
                md4.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }

            bytes = BitConverter.GetBytes(objectID.localIdentifierInFile);
            md4.TransformFinalBlock(bytes, 0, bytes.Length);
            var hash = BitConverter.ToInt64(md4.Hash, 0);
            return hash;
        }

        private static int Compare(ObjectIdentifier x, ObjectIdentifier y)
        {
            if (x.guid != y.guid)
                return x.guid.CompareTo(y.guid);

            // Notes: Only if both guids are invalid, we should check path first
            var empty = new GUID();
            if (x.guid == empty && y.guid == empty)
                return x.filePath.CompareTo(y.filePath);

            if (x.localIdentifierInFile != y.localIdentifierInFile)
                return x.localIdentifierInFile.CompareTo(y.localIdentifierInFile);

            return x.fileType.CompareTo(y.fileType);
        }

        private static int Compare(BuildCommandSet.SerializationInfo x, BuildCommandSet.SerializationInfo y)
        {
            if (x.serializationIndex != y.serializationIndex)
                return x.serializationIndex.CompareTo(y.serializationIndex);

            return Compare(x.serializationObject, y.serializationObject);
        }
    }
}