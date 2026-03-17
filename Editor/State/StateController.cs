// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;

namespace GameBooom.Editor.State
{
    internal class StateController : IStateController
    {
        private readonly Stack<GameBooomState> _stateHistory = new Stack<GameBooomState>();
        private GameBooomState _currentState = GameBooomState.Initialized;

        public GameBooomState CurrentState => _currentState;
        public bool IsInitialized => _currentState == GameBooomState.Initialized;

        public event Action<GameBooomState> OnStateChanged;
        public event Action OnCancelRequested;

        public void SetState(GameBooomState state)
        {
            if (_currentState == state) return;

            _stateHistory.Push(_currentState);
            _currentState = state;
            OnStateChanged?.Invoke(state);
        }

        public void ReturnToPreviousState()
        {
            _currentState = _stateHistory.Count > 0
                ? _stateHistory.Pop()
                : GameBooomState.Initialized;

            OnStateChanged?.Invoke(_currentState);
        }

        public void ClearState()
        {
            _stateHistory.Clear();
            _currentState = GameBooomState.Initialized;
            OnStateChanged?.Invoke(_currentState);
        }

        public void RequestCancel()
        {
            OnCancelRequested?.Invoke();
            ReturnToPreviousState();
        }
    }
}
