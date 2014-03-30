﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using Microsoft.Isam.Esent.Collections.Generic;
using Microsoft.Isam.Esent.Interop;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Storage.Esent
{
    public class UtxoBuilderStorage : IUtxoBuilderStorage
    {
        private readonly Logger logger;
        private readonly string directory;
        private readonly PersistentByteDictionary unspentTransactions;
        private readonly PersistentByteDictionary unspentOutputs;
        private bool closed = false;

        public UtxoBuilderStorage(IUtxoStorage parentUtxo, Logger logger)
        {
            this.logger = logger;
            this.directory = GetDirectory();
            if (parentUtxo is UtxoStorage)
            {
                ((UtxoStorage)parentUtxo).Duplicate(this.directory);
                this.unspentTransactions = new PersistentByteDictionary(this.directory + "_tx");
                this.unspentOutputs = new PersistentByteDictionary(this.directory + "_output");
            }
            else
            {
                this.unspentTransactions = new PersistentByteDictionary(this.directory + "_tx");
                this.unspentOutputs = new PersistentByteDictionary(this.directory + "_output");

                foreach (var unspentTx in parentUtxo.UnspentTransactions())
                    this.AddTransaction(unspentTx.Key, unspentTx.Value);

                foreach (var unspentOutput in parentUtxo.UnspentOutputs())
                    this.AddOutput(unspentOutput.Key, unspentOutput.Value);
            }
        }

        ~UtxoBuilderStorage()
        {
            this.Dispose();
        }

        public int TransactionCount
        {
            get { return this.unspentTransactions.Count; }
        }

        public bool ContainsTransaction(UInt256 txHash)
        {
            return this.unspentTransactions.ContainsKey(txHash.ToByteArray());
        }

        public bool TryGetTransaction(UInt256 txHash, out UnspentTx unspentTx)
        {
            byte[] bytes;
            if (this.unspentTransactions.TryGetValue(txHash.ToByteArray(), out bytes))
            {
                unspentTx = StorageEncoder.DecodeUnspentTx(txHash, bytes);
                return true;
            }
            else
            {
                unspentTx = default(UnspentTx);
                return false;
            }
        }

        public void AddTransaction(UInt256 txHash, UnspentTx unspentTx)
        {
            this.unspentTransactions.Add(txHash.ToByteArray(), StorageEncoder.EncodeUnspentTx(unspentTx));
        }

        public bool RemoveTransaction(UInt256 txHash)
        {
            return this.unspentTransactions.Remove(txHash.ToByteArray());
        }

        public void UpdateTransaction(UInt256 txHash, UnspentTx unspentTx)
        {
            this.unspentTransactions[txHash.ToByteArray()] = StorageEncoder.EncodeUnspentTx(unspentTx);
        }

        public IEnumerable<KeyValuePair<UInt256, UnspentTx>> UnspentTransactions()
        {
            return this.unspentTransactions.Select(keyPair =>
            {
                var txHash = new UInt256(keyPair.Key);
                var unspentTx = StorageEncoder.DecodeUnspentTx(txHash, keyPair.Value);

                return new KeyValuePair<UInt256, UnspentTx>(txHash, unspentTx);
            });
        }

        public int OutputCount
        {
            get { return this.unspentOutputs.Count; }
        }

        public bool ContainsOutput(TxOutputKey txOutputKey)
        {
            return this.unspentOutputs.ContainsKey(StorageEncoder.EncodeTxOutputKey(txOutputKey));
        }

        public bool TryGetOutput(TxOutputKey txOutputKey, out TxOutput txOutput)
        {
            byte[] bytes;
            if (this.unspentOutputs.TryGetValue(StorageEncoder.EncodeTxOutputKey(txOutputKey), out bytes))
            {
                txOutput = StorageEncoder.DecodeTxOutput(bytes);
                return true;
            }
            else
            {
                txOutput = default(TxOutput);
                return false;
            }
        }

        public void AddOutput(TxOutputKey txOutputKey, TxOutput txOutput)
        {
            this.unspentOutputs.Add(StorageEncoder.EncodeTxOutputKey(txOutputKey), StorageEncoder.EncodeTxOutput(txOutput));
        }

        public bool RemoveOutput(TxOutputKey txOutputKey)
        {
            return this.unspentOutputs.Remove(StorageEncoder.EncodeTxOutputKey(txOutputKey));
        }

        public IEnumerable<KeyValuePair<TxOutputKey, TxOutput>> UnspentOutputs()
        {
            return this.unspentOutputs.Select(keyPair =>
            {
                var txOutputKey = StorageEncoder.DecodeTxOutputKey(keyPair.Key);
                var txOutput = StorageEncoder.DecodeTxOutput(keyPair.Value);

                return new KeyValuePair<TxOutputKey, TxOutput>(txOutputKey, txOutput);
            });
        }

        public void Flush()
        {
            this.unspentTransactions.Flush();
            this.unspentOutputs.Flush();
        }

        public IUtxoStorage ToImmutable(UInt256 blockHash)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // prepare destination directory
            var destPath = UtxoStorage.GetDirectory(blockHash);
            UtxoStorage.DeleteUtxoDirectory(destPath);
            Directory.CreateDirectory(destPath + "_tx");
            Directory.CreateDirectory(destPath + "_output");

            // get database instances
            var instanceField = typeof(PersistentDictionary<string, string>)
                .GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
            var unspentTransactionsInstance = (Instance)instanceField.GetValue(this.unspentTransactions.RawDictionary);
            var unspentOutputsInstance = (Instance)instanceField.GetValue(this.unspentOutputs.RawDictionary);

            // backup to destination directory
            using (var session = new Session(unspentTransactionsInstance))
                Api.JetBackupInstance(unspentTransactionsInstance, destPath + "_tx", BackupGrbit.Atomic, null);
            using (var session = new Session(unspentOutputsInstance))
                Api.JetBackupInstance(unspentOutputsInstance, destPath + "_output", BackupGrbit.Atomic, null);

            // log snapshot info
            this.logger.Info("Saved UTXO snapshot for {0} in: {1}".Format2(blockHash, stopwatch.Elapsed));

            // return saved utxo
            return new UtxoStorage(blockHash);
        }

        public IUtxoStorage Close(UInt256 blockHash)
        {
            // close builder
            this.closed = true;
            this.Dispose();

            // prepare destination directory
            var destPath = UtxoStorage.GetDirectory(blockHash);
            UtxoStorage.DeleteUtxoDirectory(destPath);
            Directory.CreateDirectory(destPath + "_tx");
            Directory.CreateDirectory(destPath + "_output");

            // move utxo to new location
            foreach (var srcFile in Directory.GetFiles(this.directory + "_tx", "*.edb"))
                File.Move(srcFile, Path.Combine(destPath + "_tx", Path.GetFileName(srcFile)));
            foreach (var srcFile in Directory.GetFiles(this.directory + "_output", "*.edb"))
                File.Move(srcFile, Path.Combine(destPath + "_output", Path.GetFileName(srcFile)));
            UtxoStorage.DeleteUtxoDirectory(this.directory);

            // return saved utxo
            return new UtxoStorage(blockHash);
        }

        public void Dispose()
        {
            if (!this.closed)
            {
                this.unspentTransactions.Dispose();
                this.unspentOutputs.Dispose();

                UtxoStorage.DeleteUtxoDirectory(this.directory);
            }
            else
            {
                this.unspentTransactions.Dispose();
                this.unspentOutputs.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private string GetDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitSharp", "utxo", Guid.NewGuid().ToString());
        }
    }
}
