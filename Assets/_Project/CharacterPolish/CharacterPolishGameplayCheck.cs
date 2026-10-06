#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

[DefaultExecutionOrder(-1000)]
public sealed class CharacterPolishGameplayCheck : MonoBehaviour
{
    float started;int previousStage=-1;CharacterSecondaryMotion motion;float maxAngle,maxSpeed;Vector3 previous;bool stable=true;
    readonly List<string> results=new List<string>();
    readonly string[] labels={"Idle","Walk","Sprint","Quick turn","Jump","Dodge","Sudden stop","Settle"};
    PlayerCameraController cameraController;float sensitivity;
    PlayerLocomotion locomotion;PlayerState playerState;bool sprintSeen,jumpSeen,dodgeSeen;bool toggleSprint;Key sprintKey,jumpKey,slideKey;
    FieldInfo cameraSensitivity;
    void Awake()
    {
        started=Time.time;previous=transform.position;motion=GetComponentInChildren<CharacterSecondaryMotion>();
        locomotion=GetComponent<PlayerLocomotion>();playerState=GetComponent<PlayerState>();
        sprintKey=locomotion.SprintAction.controls.OfType<UnityEngine.InputSystem.Controls.KeyControl>().First().keyCode;
        jumpKey=locomotion.JumpAction.controls.OfType<UnityEngine.InputSystem.Controls.KeyControl>().First().keyCode;
        toggleSprint=(bool)typeof(PlayerLocomotion).GetField("toggleSprint",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(locomotion);
        slideKey=GetComponent<PlayerInputSettings>().Scheme==PlayerInputSettings.ControlScheme.Alt?Key.Space:Key.LeftShift;
        cameraController=GetComponent<PlayerCameraController>();
        if(cameraController)
        {
            var type=typeof(PlayerCameraController);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            cameraSensitivity=type.GetField("mouseSensitivity",flags);sensitivity=(float)cameraSensitivity.GetValue(cameraController);cameraSensitivity.SetValue(cameraController,0f);
            type.GetField("pitch",flags).SetValue(cameraController,18f);
            type.GetField("yaw",flags).SetValue(cameraController,transform.eulerAngles.y);
        }
    }
    void Update()
    {
        float elapsed=Time.time-started;int stage=Mathf.FloorToInt(elapsed/3f);
        if(stage>=labels.Length)
        {
            results.Add(labels[7]+": max secondary angle "+maxAngle.ToString("F2")+" degrees, max player speed "+maxSpeed.ToString("F2")+", stable="+stable);
            results.Add("Verified gameplay states: sprint="+sprintSeen+", jumping="+jumpSeen+", dodge="+dodgeSeen);
            Release();
            File.WriteAllLines(@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\Polish_Gameplay_Test.txt",results);
            Debug.Log("Character polish gameplay check complete: "+string.Join(" | ",results));enabled=false;return;
        }
        bool pulse=stage!=previousStage;
        if(pulse)
        {
            if(previousStage>=0)results.Add(labels[previousStage]+": max secondary angle "+maxAngle.ToString("F2")+" degrees, max player speed "+maxSpeed.ToString("F2")+", stable="+stable);
            previousStage=stage;maxAngle=maxSpeed=0;
        }
        var keys=new List<Key>();
        if(stage==1 && elapsed%3<1.2f)keys.Add(Key.W);
        if(stage==2){if(elapsed%3<1.6f)keys.Add(elapsed%3<.8f?Key.W:Key.S);if(!toggleSprint || elapsed%3<.20f)keys.Add(sprintKey);}
        if(stage==3)keys.Add(elapsed%3<.75f?Key.W:elapsed%3<1.5f?Key.D:elapsed%3<2.25f?Key.S:Key.A);
        if(stage==4 && elapsed%3<.20f)keys.Add(jumpKey);
        if(stage==5){if(elapsed%3<.6f)keys.Add(Key.D);if(elapsed%3<.20f)keys.Add(slideKey);}
        if(stage==6 && elapsed%3<.35f)keys.Add(Key.W);
        if(Keyboard.current!=null)InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(keys.ToArray()));
        maxSpeed=Mathf.Max(maxSpeed,(transform.position-previous).magnitude/Mathf.Max(Time.deltaTime,.0001f));previous=transform.position;
        if(motion){maxAngle=Mathf.Max(maxAngle,motion.MaximumAngle);stable&=motion.IsStable;}
        if(locomotion){sprintSeen|=locomotion.Sprinting;jumpSeen|=locomotion.VerticalSpeed>1f;}
        var dodge=GetComponent<DodgeController>();if(dodge)dodgeSeen|=dodge.IsDodging;
    }
    void OnGUI()
    {
        if(!enabled)return;GUI.Box(new Rect(15,15,350,58),"Character polish check: "+labels[Mathf.Clamp(previousStage,0,7)]+"\nSecondary angle: "+maxAngle.ToString("F1")+"° | stable: "+stable);
    }
    void Release(){if(Keyboard.current!=null)InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());}
    void OnDisable(){Release();if(cameraController && cameraSensitivity!=null)cameraSensitivity.SetValue(cameraController,sensitivity);}
}
#endif
