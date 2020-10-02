#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;
using Windows.UI.Input;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Uno.Extensions;
using Uno.Logging;

namespace Windows.UI.Xaml
{
	internal class DropUITarget : ICoreDropOperationTarget
	{
		private static readonly GetHitTestability _getDropHitTestability = elt =>
		{
			var visiblity = elt.GetHitTestVisibility();
			return visiblity switch
			{
				HitTestability.Collapsed => HitTestability.Collapsed,
				_ when !elt.AllowDrop => HitTestability.Invisible,
				_ => visiblity
			};
		};
			

		// Note: As drag events are routed (so they may be received by multiple elements), we might not have an entry for each drop targets.
		//		 We will instead have entry only for leaf (a.k.a. OriginalSource).
		//		 This is valid as UWP does clear the UIOverride as soon as a DragLeave is raised.
		private readonly Dictionary<UIElement, (DragUIOverride uiOverride, DataPackageOperation acceptedOperation)> _pendingDropTargets
			= new Dictionary<UIElement, (DragUIOverride uiOverride, DataPackageOperation acceptedOperation)>();

		private readonly Window _window;

		public DropUITarget(Window window)
		{
			_window = window;
		}

		/// <inheritdoc />
		public IAsyncOperation<DataPackageOperation> EnterAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
			=> EnterOrOverAsync(dragInfo, dragUIOverride);

		/// <inheritdoc />
		public IAsyncOperation<DataPackageOperation> OverAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
			=> EnterOrOverAsync(dragInfo, dragUIOverride);

		private IAsyncOperation<DataPackageOperation> EnterOrOverAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
			=> AsyncOperation.FromTask(async ct =>
			{
				if (!(dragInfo.PointerRoutedEventArgs is PointerRoutedEventArgs ptArgs))
				{
					this.Log().Error("The pointer event args was not set. Impossible to raise the drag args on the view.");
					return DataPackageOperation.None;
				}

				var target = await UpdateTarget(dragInfo, dragUIOverride, ptArgs, ct);
				if (!target.HasValue)
				{
					return DataPackageOperation.None;
				}

				var (element, args) = target.Value;
				element.RaiseDragEnterOrOver(args);

				if (args.Deferral is { } deferral)
				{
					await deferral.Completed(ct);
				}

				UpdateState(args);

				return args.AcceptedOperation;
			});

		/// <inheritdoc />
		public IAsyncAction LeaveAsync(CoreDragInfo dragInfo)
			=> AsyncAction.FromTask(async ct =>
			{
				if (!(dragInfo.PointerRoutedEventArgs is PointerRoutedEventArgs ptArgs))
				{
					this.Log().Error("The pointer event args was not set. Impossible to raise the drag args on the view.");
					return;
				}

				var leaveTasks = _pendingDropTargets.ToArray().Select(RaiseLeave);
				_pendingDropTargets.Clear();
				Task.WhenAll(leaveTasks);

				async Task RaiseLeave(KeyValuePair<UIElement, (DragUIOverride uiOverride, DataPackageOperation acceptedOperation)> target)
				{
					var args = new DragEventArgs(target.Key, dragInfo, target.Value.uiOverride, ptArgs);

					target.Key.RaiseDragLeave(args);

					if (args.Deferral is { } deferral)
					{
						await deferral.Completed(ct);
					}
				}
			});

		/// <inheritdoc />
		public IAsyncOperation<DataPackageOperation> DropAsync(CoreDragInfo dragInfo)
			=> AsyncOperation.FromTask(async ct =>
			{
				if (!(dragInfo.PointerRoutedEventArgs is PointerRoutedEventArgs ptArgs))
				{
					this.Log().Error("The pointer event args was not set. Impossible to raise the drag args on the view.");
					return DataPackageOperation.None;
				}

				var target = await UpdateTarget(dragInfo, null, ptArgs, ct);
				if (!target.HasValue)
				{
					return DataPackageOperation.None;
				}

				var (element, args) = target.Value;
				element.RaiseDrop(args);

				if (args.Deferral is { } deferral)
				{
					await deferral.Completed(ct);
				}

				return args.AcceptedOperation;
			});

		private async Task<(UIElement element, DragEventArgs args)?> UpdateTarget(
			CoreDragInfo dragInfo,
			CoreDragUIOverride? dragUIOverride,
			PointerRoutedEventArgs ptArgs,
			CancellationToken ct)
		{
			var target = VisualTreeHelper.HitTest(
				dragInfo.Position,
				getTestability: _getDropHitTestability,
				isStale: elt => elt.IsDragOver(ptArgs.Pointer));

			// First raise the drag leave event on stale branch if any.
			if (target.stale is { } staleBranch)
			{
				var leftElements = staleBranch
					.EnumerateLeafToRoot()
					.Select(elt => (isDragOver: _pendingDropTargets.TryGetValue(staleBranch.Leaf, out var dragState), elt, dragState))
					.Where(t => t.isDragOver)
					.ToArray();

				Debug.Assert(leftElements.Length > 0);

				if (leftElements.Length > 0)
				{
					// TODO: We should raise the event only from the Leaf to the Root of the branch, not the whole tree like that
					//		 This is acceptable as a MVP as we usually have only one Drop target par app.
					//		 Anyway if we Leave a bit too much, we will Enter again below
					var leaf = leftElements.First();
					var leaveArgs = new DragEventArgs(leaf.elt, dragInfo, leaf.dragState.uiOverride, ptArgs);

					staleBranch.Leaf.RaiseDragLeave(leaveArgs);

					if (leaveArgs.Deferral is { } deferral)
					{
						await deferral.Completed(ct);
					}
				}
			}

			if (target.element is null)
			{
				return null;
			}

			DragEventArgs args;
			if (target.element is {} && _pendingDropTargets.TryGetValue(target.element, out var state))
			{
				args = new DragEventArgs(target.element!, dragInfo, state.uiOverride, ptArgs)
				{
					AcceptedOperation = state.acceptedOperation
				};
			}
			else
			{
				args = new DragEventArgs(target.element!, dragInfo, new DragUIOverride(dragUIOverride ?? new CoreDragUIOverride()), ptArgs);
			}

			return (target.element!, args);
		}

		private void UpdateState(DragEventArgs args)
		{
			_pendingDropTargets[(UIElement)args.OriginalSource] = (args.DragUIOverride, args.AcceptedOperation);
		}
	}
}
