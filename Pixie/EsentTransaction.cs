﻿//-----------------------------------------------------------------------
// <copyright file="EsentTransaction.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.
// </copyright>
//-----------------------------------------------------------------------

using System;
using Microsoft.Isam.Esent.Interop;

namespace Microsoft.Isam.Esent
{
    /// <summary>
    /// A class that encapsulates an Esent transaction. Transactions are linked
    /// together in an level0Transaction -> subTransaction chain. Committing or
    /// rolling back a transaction performs the same operation on its subtransactions.
    /// </summary>
    internal class EsentTransaction : Transaction
    {
        /// <summary>
        /// The name of the transaction.
        /// </summary>
        private readonly string name;

        /// <summary>
        /// The underlying Session
        /// </summary>
        private readonly JET_SESID sesid;

        /// <summary>
        /// The previous transaction. This is the last transaction started before
        /// this transaction. If the previous transaction rolls back the work done
        /// by this transaction will be undone as well.
        /// </summary>
        private readonly EsentTransaction outerTransaction;

        /// <summary>
        /// The next transaction. This is the transaction started after this
        /// transaction. The next transaction should be committed or rolled back
        /// before the current transaction.
        /// </summary>
        private EsentTransaction subTransaction;

        /// <summary>
        /// Has this object been disposed?
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// Is this object currently in a transaction?
        /// </summary>
        private bool inTransaction;

        /// <summary>
        /// Initializes a new instance of the EsentTransaction class. This automatically
        /// begins a transaction. The transaction will be rolled back if
        /// not explicitly committed.
        /// </summary>
        /// <param name="sesid">The sesid to start the transaction for.</param>
        /// <param name="name">The name of the transaction.</param>
        /// <param name="outerTransaction">The previous active transaction.</param>
        internal EsentTransaction(JET_SESID sesid, string name, EsentTransaction outerTransaction)
        {
            this.name = name;
            this.Tracer = new Tracer("Transaction", "Esent Transaction", this.ToString());
            this.sesid = sesid;
            this.outerTransaction = outerTransaction;
            this.Begin();
            if (null != this.outerTransaction)
            {
                this.outerTransaction.RegisterSubtransaction(this);
            }

            this.Tracer.TraceVerbose("created");
        }

        /// <summary>
        /// Occurs when the transaction rolls back.
        /// </summary>
        public override event Action RolledBack;

        /// <summary>
        /// Occurs when the transaction commits.
        /// </summary>
        public override event Action Committed;

        /// <summary>
        /// Gets or sets the Tracer object for this Table.
        /// </summary>
        private Tracer Tracer { get; set; }

        /// <summary>
        /// Returns a string which names the transaction.
        /// </summary>
        /// <returns>The name of the transaction.</returns>
        public override string ToString()
        {
            return String.Format("Transaction '{0}'", this.name);
        }

        /// <summary>
        /// Disposes of an instance of the Transaction class. This will rollback the transaction
        /// if still active.
        /// </summary>
        public override void Dispose()
        {
            if (this.inTransaction)
            {
                this.Tracer.TraceWarning("transaction rolled back by dispose");

                if (null != this.subTransaction)
                {
                    this.Tracer.TraceWarning("subtransaction disposed");

                    // subTransaction.Rollback() raises the Committed event which will
                    // call UnregisterSubTransaction.
                    this.subTransaction.Dispose();
                }

                this.Rollback();
            }

            this.isDisposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Commit a transaction. This object must be in a transaction.
        /// </summary>
        public override void Commit()
        {
            this.Commit(false);
        }

        /// <summary>
        /// Commit a transaction. This object must be in a transaction.
        /// </summary>
        /// <param name="commitIsLazy">
        /// Makes the commit lazy. A lazy commit doesn't synchronously flush the
        /// commit log record to the disk.
        /// </param>
        public override void Commit(bool commitIsLazy)
        {
            this.CheckNotDisposed();
            if (!this.inTransaction)
            {
                throw new InvalidOperationException("Not in a transaction");
            }

            if (null != this.subTransaction)
            {
                this.Tracer.TraceWarning("committing subtransaction");

                // subTransaction.Commit() raises the Committed event which will
                // call UnregisterSubTransaction.
                this.subTransaction.Commit();
            }

            if (null != this.Committed)
            {
                this.Committed();
            }

            CommitTransactionGrbit grbit = commitIsLazy ? CommitTransactionGrbit.LazyFlush : CommitTransactionGrbit.None;
            Api.JetCommitTransaction(this.sesid, grbit);

            // Even if this transaction commits the work done in the transaction will 
            // be undone if the outer transaction is rolled back. To make sure that the
            // correct events are sent we transfer our rollback delegates to the outer
            // transaction.
            this.TransferRollbackDelegatesToOuterTransaction();

            this.inTransaction = false;
            this.Tracer.TraceVerbose("commit ({0})", grbit);
        }

        /// <summary>
        /// Rollback a transaction. This object must be in a transaction.
        /// </summary>
        public override void Rollback()
        {
            this.CheckNotDisposed();
            if (!this.inTransaction)
            {
                throw new InvalidOperationException("Not in a transaction");
            }

            if (null != this.subTransaction)
            {
                this.Tracer.TraceWarning("rolling back subtransaction");

                // subTransaction.Rollback() raises the Committed event which will
                // call UnregisterSubTransaction.
                this.subTransaction.Rollback();
            }

            if (null != this.RolledBack)
            {
                this.RolledBack();
            }

            Api.JetRollback(this.sesid, RollbackTransactionGrbit.None);
            this.inTransaction = false;

            this.Tracer.TraceWarning("rollback");
        }

        /// <summary>
        /// Register the a transaction as a subtransaction. A subtransaction has to
        /// commit or rollback before this transaction.
        /// </summary>
        /// <param name="transaction">The new subtransaction.</param>
        internal void RegisterSubtransaction(EsentTransaction transaction)
        {
            this.subTransaction = transaction;
            this.subTransaction.Committed += this.UnregisterSubtransaction;
            this.subTransaction.RolledBack += this.UnregisterSubtransaction;
        }

        /// <summary>
        /// Returns the newest (innermost) transaction.
        /// </summary>
        /// <returns>The newest (innermost) transaction.</returns>
        internal EsentTransaction GetNewestTransaction()
        {
            if (null == this.subTransaction)
            {
                return this;
            }

            return this.subTransaction.GetNewestTransaction();
        }

        /// <summary>
        /// Unregister the subtransaction. This should be called when the subtransaction
        /// either commits or rolls back.
        /// </summary>
        private void UnregisterSubtransaction()
        {
            this.subTransaction = null;
        }

        /// <summary>
        /// Register all the RolledBack event delegates with the RolledBack event
        /// of the outer transaction.
        /// </summary>
        private void TransferRollbackDelegatesToOuterTransaction()
        {
            if (null != this.outerTransaction && null != this.RolledBack)
            {
                foreach (Action action in this.RolledBack.GetInvocationList())
                {
                    this.outerTransaction.RolledBack += action;
                }
            }
        }

        /// <summary>
        /// Begin a transaction. This object should not currently be
        /// in a transaction.
        /// </summary>
        private void Begin()
        {
            this.CheckNotDisposed();
            if (this.inTransaction)
            {
                throw new InvalidOperationException("Already in a transaction");
            }

            Api.JetBeginTransaction(this.sesid);
            this.inTransaction = true;
        }

        /// <summary>
        /// Throw an exception if this object has been disposed.
        /// </summary>
        private void CheckNotDisposed()
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException("Transaction");
            }
        }    
    }
}