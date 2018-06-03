﻿using System;
using Rubberduck.Refactorings;

namespace Rubberduck.UI.Refactorings
{
    public class RefactoringPresenterBase<TModel, TDialog, TView, TViewModel> : IDisposable
        where TModel : class
        where TView : System.Windows.Controls.UserControl, new()
        where TViewModel : RefactoringViewModelBase<TModel>
        where TDialog : RefactoringDialogBase<TModel, TView, TViewModel>
    {
        private readonly TDialog _dialog;
        private readonly IRefactoringDialogFactory<TModel, TView, TViewModel, TDialog> _factory;

        public RefactoringPresenterBase(TModel model, IRefactoringDialogFactory<TModel, TView, TViewModel, TDialog> factory)
        {
            _factory = factory;
            var viewModel = _factory.CreateViewModel(model);
            _dialog = _factory.CreateDialog(viewModel);
        }

        public TModel Model => _dialog.ViewModel.Model;
        public TDialog Dialog => _dialog;
        public TViewModel ViewModel => _dialog.ViewModel;
        public virtual RefactoringDialogResult DialogResult { get; protected set; }

        public virtual TModel Show()
        {
            DialogResult = _dialog.ShowDialog();
            return DialogResult == RefactoringDialogResult.Execute ? _dialog.ViewModel.Model : null;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _factory.ReleaseViewModel(_dialog.ViewModel);
                _factory.ReleaseDialog(_dialog);
                _dialog.Dispose();
            }
        }
    }
}
