﻿using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Newtonsoft.Json;

public abstract class MetaballEditorComponentBase<TEditor, TCombinedAction, TAction, TMetaball> :
    EditorComponentWithActionsBase<TEditor, TCombinedAction>,
    ISaveLoadedTracked, IChildPropertiesLoadCallback
    where TEditor : class, IHexEditor, IEditorWithActions
    where TCombinedAction : CombinedEditorAction
    where TAction : EditorAction
    where TMetaball : Metaball
{
    [Export]
    public NodePath CameraPath = null!;

    [Export]
    public NodePath EditorArrowPath = null!;

    [Export]
    public NodePath EditorGroundPath = null!;

    [Export]
    public NodePath IslandErrorPath = null!;

    /// <summary>
    ///   Set above 0 to make sure the arrow doesn't overlap with the ground circle graphics
    /// </summary>
    [Export]
    public float ForwardArrowOffsetFromGround = 0.1f;

    protected EditorCamera3D? camera;

    [JsonIgnore]
    protected MeshInstance editorArrow = null!;

    protected MeshInstance editorGround = null!;

    protected AudioStream hexPlacementSound = null!;

    [JsonProperty]
    protected string? activeActionName;

    /// <summary>
    ///   This is a global assessment if the currently being placed thing / action is valid
    /// </summary>
    protected bool isPlacementProbablyValid;

    /// <summary>
    ///   This is used to keep track of used hover metaballs
    /// </summary>
    protected int usedHoverMetaballIndex;

    protected bool hoverMetaballsChanged = true;

    protected List<TMetaball> hoverMetaballData = new();

    protected IMetaballDisplayer<TMetaball>? alreadyPlacedVisuals;
    protected IMetaballDisplayer<TMetaball>? hoverMetaballDisplayer;

    private const float DefaultHoverAlpha = 0.7f;
    private const float CannotPlaceHoverAlpha = 0.7f;

    private CustomConfirmationDialog islandPopup = null!;

    private HexEditorSymmetry symmetry = HexEditorSymmetry.None;

    private IEnumerable<(Vector3 Position, TMetaball? Parent)>? mouseHoverPositions;

    /// <summary>
    ///   The symmetry setting of the editor.
    /// </summary>
    [JsonProperty]
    public HexEditorSymmetry Symmetry
    {
        get => symmetry;
        set
        {
            symmetry = value;

            if (symmetry != HexEditorSymmetry.None)
                throw new NotSupportedException("Symmetry editing not implemented yet");
        }
    }

    /// <summary>
    ///   Metaball that is in the process of being moved but a new location hasn't been selected yet.
    ///   If null, nothing is in the process of moving.
    /// </summary>
    [JsonProperty]
    public TMetaball? MovingPlacedMetaball { get; protected set; }

    // TODO: implement 3D editor camera position and rotation saving

    [JsonIgnore]
    public IEnumerable<(Vector3 Position, TMetaball? Parent)>? MouseHoverPositions
    {
        get => mouseHoverPositions;
        set
        {
            if (mouseHoverPositions == null && value == null)
                return;

            if (mouseHoverPositions != null && value != null && mouseHoverPositions.SequenceEqual(value))
                return;

            mouseHoverPositions = value;
            UpdateMutationPointsBar();
        }
    }

    /// <summary>
    ///   If true a hex move is in progress and can be canceled
    /// </summary>
    [JsonIgnore]
    public bool CanCancelMove => MovingPlacedMetaball != null;

    [JsonIgnore]
    public override bool CanCancelAction => CanCancelMove;

    [JsonIgnore]
    public abstract bool HasIslands { get; }

    public bool IsLoadedFromSave { get; set; }

    protected abstract bool ForceHideHover { get; }

    public override void _Ready()
    {
        base._Ready();

        ResolveNodeReferences();

        LoadScenes();
        LoadAudioStreams();
    }

    public virtual void ResolveNodeReferences()
    {
        islandPopup = GetNode<CustomConfirmationDialog>(IslandErrorPath);

        if (IsLoadedFromSave)
        {
            // When directly loaded from the base scene (which is done when loading from a save), some of our
            // node paths are not set so we need to skip them
            return;
        }

        camera = GetNode<EditorCamera3D>(CameraPath);
        editorArrow = GetNode<MeshInstance>(EditorArrowPath);
        editorGround = GetNode<MeshInstance>(EditorGroundPath);

        camera.Connect(nameof(EditorCamera3D.OnPositionChanged), this, nameof(OnCameraPositionChanged));
    }

    public override void Init(TEditor owningEditor, bool fresh)
    {
        base.Init(owningEditor, fresh);

        if (camera == null)
        {
            throw new InvalidOperationException(
                "This editor component was loaded from a save and is not fully functional");
        }

        if (fresh)
        {
            ResetSymmetryButton();
        }

        UpdateSymmetryIcon();

        LoadMetaballDisplayers();

        hoverMetaballsChanged = true;
        hoverMetaballData.Clear();
    }

    public void ResetSymmetryButton()
    {
        componentBottomLeftButtons.ResetSymmetry();
        symmetry = 0;
    }

    /// <summary>
    ///   Set tab specific editor world object visibility
    /// </summary>
    /// <param name="shown">True if they should be visible</param>
    public virtual void SetEditorWorldTabSpecificObjectVisibility(bool shown)
    {
        SetEditorWorldGuideObjectVisibility(shown);

        if (alreadyPlacedVisuals != null)
        {
            alreadyPlacedVisuals.Visible = shown;
            hoverMetaballDisplayer!.Visible = shown;
        }
    }

    public void SetEditorWorldGuideObjectVisibility(bool shown)
    {
        editorArrow.Visible = shown;
        editorGround.Visible = shown;
    }

    /// <summary>
    ///   Updates the background shown in the editor
    /// </summary>
    public void UpdateBackgroundImage(Biome biomeToUseBackgroundFrom)
    {
        GD.Print("TODO: 3D editor background for patches");
    }

    [RunOnKeyDown("e_primary")]
    public virtual bool PerformPrimaryAction()
    {
        if (!Visible)
            return false;

        throw new NotImplementedException();

        if (MovingPlacedMetaball != null)
        {
            GetMouseHex(out int q, out int r);
            PerformMove(q, r);
        }
        else
        {
            if (string.IsNullOrEmpty(activeActionName))
                return true;

            PerformActiveAction();
        }

        return true;
    }

    /// <summary>
    ///   Cancels the current editor action
    /// </summary>
    /// <returns>True when the input is consumed</returns>
    [RunOnKeyDown("e_cancel_current_action", Priority = 1)]
    public bool CancelCurrentAction()
    {
        if (!Visible)
            return false;

        if (MovingPlacedMetaball != null)
        {
            OnCurrentActionCanceled();

            // Re-enable undo/redo button
            Editor.NotifyUndoRedoStateChanged();

            return true;
        }

        return false;
    }

    /// <summary>
    ///   Begin hex movement under the cursor
    /// </summary>
    [RunOnKeyDown("e_move")]
    public bool StartHexMoveAtCursor()
    {
        if (!Visible)
            return false;

        // Can't move anything while already moving one
        if (MovingPlacedMetaball != null)
        {
            Editor.OnActionBlockedWhileMoving();
            return true;
        }

        throw new NotImplementedException();

        /*
        GetMouseHex(out int q, out int r);

        var hex = GetHexAt(new Hex(q, r));

        if (hex == null)
            return true;

        StartHexMove(hex);

        // Once a move has begun, the button visibility should be updated so it becomes visible
        UpdateCancelState();
        return true;
        */
    }

    public void StartHexMove(TMetaball selectedHex)
    {
        if (MovingPlacedMetaball != null)
        {
            // Already moving something! some code went wrong
            throw new InvalidOperationException("Can't begin hex move while another in progress");
        }

        MovingPlacedMetaball = selectedHex;

        OnMoveActionStarted();

        // Disable undo/redo/symmetry button while moving (enabled after finishing move)
        Editor.NotifyUndoRedoStateChanged();

        // TODO: change this to go through the editor as well for consistency
        UpdateSymmetryButton();
    }

    public void StartHexMoveWithSymmetry(IEnumerable<TMetaball> selectedHexes)
    {
        // TODO: implement symmetry move for metaballs (note also not implemented for hex editor yet)
        throw new NotImplementedException();
    }

    public void RemoveHex(Hex hex)
    {
        var actions = new List<TAction>();
        int alreadyDeleted = 0;

        RunWithSymmetry(hex.Q, hex.R, (q, r, _) =>
        {
            var removed = TryCreateRemoveHexAtAction(new Hex(q, r), ref alreadyDeleted);

            if (removed != null)
                actions.Add(removed);
        });

        if (actions.Count < 1)
            return;

        var combinedAction = CreateCombinedAction(actions);

        EnqueueAction(combinedAction);
    }

    /// <summary>
    ///   Remove the hex under the cursor (if there is one)
    /// </summary>
    [RunOnKeyDown("e_delete")]
    public void RemoveHexAtCursor()
    {
        throw new NotImplementedException();

        /*
        GetMouseHex(out int q, out int r);

        Hex mouseHex = new Hex(q, r);

        var hex = GetHexAt(mouseHex);

        if (hex == null)
            return;

        RemoveHex(mouseHex);
        */
    }

    public override bool CanFinishEditing(IEnumerable<EditorUserOverride> userOverrides)
    {
        if (!base.CanFinishEditing(userOverrides))
            return false;

        // Can't exit the editor with disconnected organelles
        if (HasIslands)
        {
            islandPopup.PopupCenteredShrink();
            return false;
        }

        return true;
    }

    public override void OnValidAction()
    {
        GUICommon.Instance.PlayCustomSound(hexPlacementSound);
    }

    public override void _Process(float delta)
    {
        base._Process(delta);

        if (hoverMetaballDisplayer == null)
            throw new InvalidOperationException($"{GetType().Name} not initialized");

        // TODO: should we display the hover metaballs setup on the previous frame here?
        if (hoverMetaballsChanged)
        {
            hoverMetaballDisplayer.OverrideColourAlpha =
                isPlacementProbablyValid ? DefaultHoverAlpha : CannotPlaceHoverAlpha;

            hoverMetaballDisplayer.DisplayFromList(hoverMetaballData);

            hoverMetaballsChanged = false;
        }

        // Clear the hover metaballs for the concrete editor type to use
        hoverMetaballsChanged = false;
        usedHoverMetaballIndex = 0;
    }

    public void OnNoPropertiesLoaded()
    {
        // Something is wrong if this is called
        throw new InvalidOperationException();
    }

    public virtual void OnPropertiesLoaded()
    {
    }

    /// <summary>
    ///   Updates the forward pointing arrow to not overlap the edited species. Should be called on any layout change
    /// </summary>
    /// <remarks>
    ///   <para>
    ///     This is public so that editors with multiple tabs can make the arrow fix itself after switching tabs
    ///   </para>
    /// </remarks>
    public void UpdateArrow(bool animateMovement = true)
    {
        var arrowPosition = new Vector3(0, ForwardArrowOffsetFromGround,
            CalculateEditorArrowZPosition() - Constants.EDITOR_ARROW_OFFSET);

        if (animateMovement)
        {
            // TODO: check that this works
            GUICommon.Instance.Tween.InterpolateProperty(editorArrow, "translation", editorArrow.Translation,
                arrowPosition, Constants.EDITOR_ARROW_INTERPOLATE_SPEED,
                Tween.TransitionType.Expo, Tween.EaseType.Out);
            GUICommon.Instance.Tween.Start();
        }
        else
        {
            editorArrow.Translation = arrowPosition;
        }
    }

    protected abstract IMetaballDisplayer<TMetaball> CreateMetaballDisplayer();

    protected virtual void LoadMetaballDisplayers()
    {
        alreadyPlacedVisuals = CreateMetaballDisplayer();
        hoverMetaballDisplayer = CreateMetaballDisplayer();
        hoverMetaballDisplayer.OverrideColourAlpha = DefaultHoverAlpha;
    }

    protected virtual void LoadScenes()
    {
    }

    protected virtual void LoadAudioStreams()
    {
        hexPlacementSound = GD.Load<AudioStream>("res://assets/sounds/soundeffects/gui/click_place_success.ogg");
    }

    protected override bool EnqueueAction(TCombinedAction action)
    {
        if (!Editor.CheckEnoughMPForAction(Editor.WhatWouldActionsCost(action.Data)))
            return false;

        if (CanCancelMove)
        {
            if (!DoesActionEndInProgressAction(action))
            {
                // Play sound
                Editor.OnActionBlockedWhileMoving();
                return false;
            }
        }

        OnMoveWillSucceed();

        Editor.EnqueueAction(action);
        Editor.OnValidAction();
        UpdateSymmetryButton();
        return true;
    }

    protected virtual TCombinedAction CreateCombinedAction(IEnumerable<EditorAction> actions)
    {
        return (TCombinedAction)new CombinedEditorAction(actions);
    }

    protected void OnSymmetryPressed()
    {
        throw new NotImplementedException("symmetry not implemented");

        /*
        if (symmetry == HexEditorSymmetry.SixWaySymmetry)
        {
            ResetSymmetryButton();
        }
        else if (symmetry == HexEditorSymmetry.None)
        {
            symmetry = HexEditorSymmetry.XAxisSymmetry;
        }
        else if (symmetry == HexEditorSymmetry.XAxisSymmetry)
        {
            symmetry = HexEditorSymmetry.FourWaySymmetry;
        }
        else if (symmetry == HexEditorSymmetry.FourWaySymmetry)
        {
            symmetry = HexEditorSymmetry.SixWaySymmetry;
        }

        Symmetry = symmetry;
        UpdateSymmetryIcon();
        */
    }

    /// <inheritdoc cref="HexEditorComponentBase{TEditor,TCombinedAction,TAction,THexMove}.OnHexEditorMouseEntered"/>
    protected void OnMetaballEditorMouseEntered()
    {
        if (!Visible)
            return;

        Editor.ShowHover = true;
        UpdateMutationPointsBar();
    }

    /// <inheritdoc cref="HexEditorComponentBase{TEditor,TCombinedAction,TAction,THexMove}.OnHexEditorMouseExited"/>
    protected void OnMetaballEditorMouseExited()
    {
        Editor.ShowHover = false;
        UpdateMutationPointsBar();
    }

    /// <inheritdoc cref="HexEditorComponentBase{TEditor,TCombinedAction,TAction,THexMove}.OnHexEditorGuiInput"/>
    protected void OnMetaballEditorGuiInput(InputEvent inputEvent)
    {
        if (!Editor.ShowHover)
            return;

        InputManager.ForwardInput(inputEvent);
    }

    /// <summary>
    ///   Returns the hex position the mouse is over
    /// </summary>
    protected void GetMouseHex(out int q, out int r)
    {
        // TODO: need to change to a ray cast from the camera
        throw new NotImplementedException();
    }

    /// <summary>
    ///   Runs given callback for all symmetry positions
    /// </summary>
    /// <param name="q">The base q</param>
    /// <param name="r">The base r value of the coordinate</param>
    /// <param name="callback">The callback that is called based on symmetry, parameters are: q, r, rotation</param>
    /// <param name="overrideSymmetry">If set, overrides the current symmetry</param>
    protected void RunWithSymmetry(int q, int r, Action<int, int, int> callback,
        HexEditorSymmetry? overrideSymmetry = null)
    {
        throw new NotImplementedException();

        overrideSymmetry ??= Symmetry;

        switch (overrideSymmetry)
        {
            case HexEditorSymmetry.None:
            {
                callback(q, r, 0);
                break;
            }

            default:
                throw new NotSupportedException("symmetry editing not implemented yet");
        }
    }

    protected virtual void OnCurrentActionCanceled()
    {
        UpdateCancelButtonVisibility();

        // TODO: switch to this going through the editor
        UpdateSymmetryButton();
    }

    protected virtual void OnMoveWillSucceed()
    {
        MovingPlacedMetaball = null;

        // Move succeeded; Update the cancel button visibility so it's hidden because the move has completed
        // TODO: should this call be made through Editor here?
        UpdateCancelButtonVisibility();

        // Re-enable undo/redo button
        Editor.NotifyUndoRedoStateChanged();
    }

    /// <summary>
    ///   Handles positioning hover hexes at the coordinates to show what is about to be places. Handles conflicts with
    ///   already placed hexes. <see cref="isPlacementProbablyValid"/> should be set to an initial good value before
    ///   calling this.
    /// </summary>
    /// <param name="q">Q coordinate</param>
    /// <param name="r">R coordinate</param>
    /// <param name="toBePlacedHexes">
    ///   List of hexes to show at the coordinates, need to have at least one to do anything useful
    /// </param>
    /// <param name="canPlace">
    ///   True if the editor logic thinks this is a valid placement (selects material for used hover hexes)
    /// </param>
    /// <param name="hadDuplicate">Set to true if an already placed hex was conflicted with</param>
    protected void RenderHoveredHex(int q, int r, IEnumerable<Hex> toBePlacedHexes, bool canPlace,
        out bool hadDuplicate)
    {
        throw new NotImplementedException();
    }

    protected void UpdateAlreadyPlacedHexes(
        IEnumerable<(Hex BasePosition, IEnumerable<Hex> Hexes, bool PlacedThisSession)> hexes, List<Hex> islands,
        bool forceHide = false)
    {
        throw new NotImplementedException();
    }

    protected abstract void PerformActiveAction();

    protected virtual bool DoesActionEndInProgressAction(TCombinedAction action)
    {
        // Allow only move actions with an in-progress move
        return action.Data.Any(d => d is MetaballMoveActionData<TMetaball>);
    }

    /// <summary>
    ///   Checks if the target position is valid to place hex.
    /// </summary>
    /// <param name="position">Position to check</param>
    /// <param name="rotation">
    ///   The rotation to check for the hex (only makes sense when placing a group of hexes)
    /// </param>
    /// <param name="hex">The move data to try to move to the position</param>
    /// <returns>True if valid</returns>
    protected abstract bool IsMoveTargetValid(Hex position, int rotation, TMetaball hex);

    protected abstract void OnMoveActionStarted();
    protected abstract void PerformMove(int q, int r);
    protected abstract TAction? TryCreateRemoveHexAtAction(Hex location, ref int alreadyDeleted);

    protected abstract float CalculateEditorArrowZPosition();

    protected virtual void UpdateCancelState()
    {
        UpdateCancelButtonVisibility();
    }

    protected void UpdateSymmetryButton()
    {
        componentBottomLeftButtons.SymmetryEnabled = MovingPlacedMetaball == null;
    }

    private void OnCameraPositionChanged(Transform newPosition)
    {
        // TODO: implement camera position saving
    }

    // TODO: make this method trigger automatically on Symmetry assignment
    private void UpdateSymmetryIcon()
    {
        componentBottomLeftButtons.SetSymmetry(symmetry);
    }
}
