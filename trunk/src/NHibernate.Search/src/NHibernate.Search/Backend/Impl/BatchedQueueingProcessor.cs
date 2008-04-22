using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NHibernate.Search.Backend.Impl.Lucene;
using NHibernate.Search.Engine;
using NHibernate.Search.Impl;
using NHibernate.Util;

namespace NHibernate.Search.Backend.Impl {
	/// <summary>
	///  Batch work until #ExecuteQueue is called.
	///  The work is then executed synchronously or asynchronously
	/// </summary>
	public class BatchedQueueingProcessor : IQueueingProcessor {
		private readonly IBackendQueueProcessorFactory backendQueueProcessorFactory;
		private readonly bool sync;
		private int batchSize = 0;
		private SearchFactory searchFactory;

		public BatchedQueueingProcessor(SearchFactory searchFactory,
		                                IDictionary properties) {
			this.searchFactory = searchFactory;
			//default to sync if none defined
			sync =
				!"async".Equals((string) properties[Environment.WorkerExecution],
				                StringComparison.InvariantCultureIgnoreCase);

			string backend = (string) properties[Environment.WorkerBackend];
			batchSize = 0; //(int)properties[Environment.WorkerBatchSize];
			if (StringHelper.IsEmpty(backend) || "lucene".Equals(backend, StringComparison.InvariantCultureIgnoreCase))
				backendQueueProcessorFactory = new LuceneBackendQueueProcessorFactory();
			else
				try {
					System.Type processorFactoryClass = ReflectHelper.ClassForName(backend);
					backendQueueProcessorFactory =
						(IBackendQueueProcessorFactory) Activator.CreateInstance(processorFactoryClass);
				}
				catch (Exception e) {
					throw new SearchException("Unable to find/create processor class: " + backend, e);
				}
			backendQueueProcessorFactory.Initialize(properties, searchFactory);
			searchFactory.SetbackendQueueProcessorFactory(backendQueueProcessorFactory);
		}

		//TODO implements parallel batchWorkers (one per Directory)

		#region IQueueingProcessor Members

		public void Add(Work work, WorkQueue workQueue) {
			workQueue.add(work);
			if (batchSize > 0 && workQueue.size() >= batchSize) {
				WorkQueue subQueue = workQueue.splitQueue();
				PrepareWorks(subQueue);
				PerformWorks(subQueue);
			}
		}

		public void PerformWorks(WorkQueue workQueue) {
			WaitCallback processor = backendQueueProcessorFactory.GetProcessor(workQueue.getSealedQueue());
			if (sync)
				processor(null);
			else
				ThreadPool.QueueUserWorkItem(processor);
		}

		public void CancelWorks(WorkQueue workQueue) {
			workQueue.clear();
		}

		public void PrepareWorks(WorkQueue workQueue) {
			List<Work> queue = workQueue.getQueue();
			int initialSize = queue.Count;
			List<LuceneWork> luceneQueue = new List<LuceneWork>(initialSize); //TODO load factor for containedIn
			/**
			 * Collection work type are process second, so if the owner entity has already been processed for whatever reason
			 * the work will be ignored.
			 * However if the owner entity has not been processed, an "UPDATE" work is executed
			 *
			 * Processing collection works last is mandatory to avoid reindexing a object to be deleted
			 */
			processWorkByLayer(queue, initialSize, luceneQueue, Layer.FIRST);
			processWorkByLayer(queue, initialSize, luceneQueue, Layer.SECOND);
			workQueue.setSealedQueue(luceneQueue);
		}

		#endregion

		private void processWorkByLayer(List<Work> queue, int initialSize, List<LuceneWork> luceneQueue, Layer layer) {
			for (int i = 0; i < initialSize; i++) {
				Work work = queue[i];
				if (work != null)
					if (layer.isRightLayer(work.WorkType)) {
						queue[i] = null; // help GC and avoid 2 loaded queues in memory
						System.Type entityClass = NHibernateUtil.GetClass(work.Entity);
						DocumentBuilder builder = searchFactory.DocumentBuilders[entityClass];
						if (builder == null) continue; //or exception?
						builder.AddToWorkQueue(work.Entity, work.Id, work.WorkType, luceneQueue, searchFactory);
					}
			}
		}

		#region Nested type: Layer

		private abstract class Layer {
			public static readonly Layer FIRST = new First();
			public static readonly Layer SECOND = new Second();
			public abstract bool isRightLayer(WorkType type);

			#region Nested type: First

			private class First : Layer {
				public override bool isRightLayer(WorkType type) {
					//return  type != WorkType.COLLECTION ;
					return true;
				}
			}

			#endregion

			#region Nested type: Second

			private class Second : Layer {
				public override bool isRightLayer(WorkType type) {
					//return type == WorkType.COLLECTION;
					return false;
				}
			}

			#endregion
		}

		#endregion
	}
}