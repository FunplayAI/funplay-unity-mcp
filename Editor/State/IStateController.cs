// Copyright (C) GameBooom. Licensed under GPLv3.

using System;

namespace GameBooom.Editor.State
{
    internal interface IStateController
    {
        GameBooomState CurrentState { get; }
        event Action<GameBooomState> OnStateChanged;
        event Action OnCancelRequested;

        void SetState(GameBooomState state);
        void ReturnToPreviousState();
        void ClearState();
        void RequestCancel();
        bool IsInitialized { get; }
    }
}
