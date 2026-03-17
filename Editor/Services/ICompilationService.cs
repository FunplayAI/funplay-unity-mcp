// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Threading.Tasks;

namespace GameBooom.Editor.Services
{
    internal interface ICompilationService
    {
        bool IsCompiling { get; }
        event Action OnCompilationFinished;
        Task<bool> WaitForCompilationAsync(bool forceRefresh, int timeoutSeconds);
        string GetCompilationErrors(int maxEntries = 50, bool includeWarnings = false);
    }
}
