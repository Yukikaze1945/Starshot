using System;
using System.Runtime.InteropServices;

namespace Starshot.Features.ViewHost;

/// <summary>UI-thread-only focus handoff. A failed focus request must never abort capture delivery.</summary>
internal sealed class DeferredWebFocus
{
    private object? _pending;
    private bool _moving;

    internal void Cancel() => _pending=null;

    internal void Request(Func<Action,bool> enqueue,Func<bool> canFocus,Action moveFocus,Action<Exception> failed)
    {
        if(_pending is not null || _moving)return;
        object ticket=new();_pending=ticket;
        try
        {
            bool accepted=enqueue(()=>
            {
                if(!ReferenceEquals(_pending,ticket))return;
                _pending=null;_moving=true;
                try { if(canFocus())moveFocus(); }
                catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or COMException){failed(ex);}
                finally { _moving=false; }
            });
            if(!accepted && ReferenceEquals(_pending,ticket))_pending=null;
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or COMException)
        {
            if(ReferenceEquals(_pending,ticket))_pending=null;
            failed(ex);
        }
    }
}
