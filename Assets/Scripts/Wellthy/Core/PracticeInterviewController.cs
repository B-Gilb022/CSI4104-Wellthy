using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Wellthy.Config;
using Wellthy.Data;

namespace Wellthy.Core
{
    /// <summary>
    /// Streamlined practice-interview backend controller for the Interview Room scene.
    /// - Button 1 ("Start/Reset Interview"): selects 4 random questions from questions.json
    ///   and starts/resets the interview from Question 1 with spoken audio.
    /// - Each question has a 120-second time limit and auto-advances when time runs out.
    /// - Button 2 ("Next Question"): skips immediately to the next question when the player
    ///   finishes answering under 120 seconds (or finishes the interview on Question 4).
    /// - Does not record or track player responses (verbal self-practice only).
    /// </summary>
    public class PracticeInterviewController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private QuestionDatabase questionDatabase;
        [SerializeField] private AudioSource interviewerAudioSource;

        [Header("Interview Rules")]
        [Tooltip("Number of questions selected for each interview session.")]
        [Min(1)]
        [SerializeField] private int questionsPerInterview = 4;

        [Tooltip("Maximum time limit (in seconds) allowed per question.")]
        [Min(1f)]
        [SerializeField] private float secondsPerQuestion = 120f;

        [Tooltip("How questions are sampled from the question bank.")]
        [SerializeField] private QuestionSelectionMode selectionMode = QuestionSelectionMode.RandomMix;

        [Header("Scene UI Buttons (2 Buttons)")]
        [SerializeField] private Button startResetButton;
        [SerializeField] private Button nextQuestionButton;

        [Header("Optional Display UI (Read-Only Status)")]
        [SerializeField] private TMP_Text statusHeaderText;
        [SerializeField] private TMP_Text questionBodyText;
        [SerializeField] private TMP_Text startResetButtonLabel;
        [SerializeField] private TMP_Text nextQuestionButtonLabel;

        [Header("End-of-Interview Self-Reflection")]
        [TextArea(5, 10)]
        [SerializeField] private string completionReflectionText =
            "Great job practicing! Press \"Start/Reset Interview\" to start a new session.\n\n" +
            "Self-Reflection Questions:\n" +
            "• What went well in my practice interview?\n" +
            "• What can I improve for my next attempt?\n" +
            "• Did I clearly cover Situation, Task, Action (\"I\" vs. \"we\"), and Result?\n" +
            "• How was my pacing, vocal confidence, and composure under the 2-minute limit?\n" +
            "• Which question felt hardest, and what should I adjust next time?";

        [Header("Events")]
        public UnityEvent onInterviewStarted = new UnityEvent();
        public UnityEvent<QuestionItem, int, int> onQuestionPresented = new UnityEvent<QuestionItem, int, int>();
        public UnityEvent<float, float> onTimerUpdated = new UnityEvent<float, float>();
        public UnityEvent<AudioClip> onInterviewerStartSpeaking = new UnityEvent<AudioClip>();
        public UnityEvent onInterviewerStopSpeaking = new UnityEvent();
        public UnityEvent onInterviewCompleted = new UnityEvent();

        private readonly List<QuestionItem> _selectedQuestions = new List<QuestionItem>();
        private int _currentQuestionIndex = -1;
        private float _questionRemainingSeconds;
        private bool _isInterviewActive;
        private bool _wasAudioPlayingLastFrame;
        private int _audioRequestVersion;

        public bool IsInterviewActive => _isInterviewActive;
        public int CurrentQuestionIndex => _currentQuestionIndex;
        public int CurrentQuestionNumber => _isInterviewActive ? _currentQuestionIndex + 1 : 0;
        public int TotalQuestions => _selectedQuestions.Count;
        public float QuestionRemainingSeconds => _questionRemainingSeconds;
        public float SecondsPerQuestion => secondsPerQuestion;
        public bool IsInterviewerSpeaking => interviewerAudioSource != null && interviewerAudioSource.isPlaying;

        public QuestionItem CurrentQuestion =>
            (_isInterviewActive && _currentQuestionIndex >= 0 && _currentQuestionIndex < _selectedQuestions.Count)
                ? _selectedQuestions[_currentQuestionIndex]
                : null;

        public IReadOnlyList<QuestionItem> SelectedQuestions => _selectedQuestions;

        private void Awake()
        {
            if (questionDatabase == null)
            {
                questionDatabase = GetComponent<QuestionDatabase>();
                if (questionDatabase == null)
                {
#if UNITY_2022_2_OR_NEWER
                    questionDatabase = FindAnyObjectByType<QuestionDatabase>();
#else
                    questionDatabase = FindObjectOfType<QuestionDatabase>();
#endif
                }
                if (questionDatabase == null)
                {
                    questionDatabase = gameObject.AddComponent<QuestionDatabase>();
                }
            }

            if (interviewerAudioSource == null)
            {
                interviewerAudioSource = GetComponent<AudioSource>();
                if (interviewerAudioSource == null)
                {
                    interviewerAudioSource = gameObject.AddComponent<AudioSource>();
                }
            }

            interviewerAudioSource.playOnAwake = false;
        }

        private void OnEnable()
        {
            if (startResetButton != null)
            {
                startResetButton.onClick.AddListener(StartOrResetInterview);
            }

            if (nextQuestionButton != null)
            {
                nextQuestionButton.onClick.AddListener(SkipToNextQuestion);
            }
        }

        private void OnDisable()
        {
            if (startResetButton != null)
            {
                startResetButton.onClick.RemoveListener(StartOrResetInterview);
            }

            if (nextQuestionButton != null)
            {
                nextQuestionButton.onClick.RemoveListener(SkipToNextQuestion);
            }
        }

        private void Start()
        {
            questionDatabase.EnsureLoaded();
            SetIdleStateUI();
        }

        private void Update()
        {
            if (_wasAudioPlayingLastFrame && !IsInterviewerSpeaking)
            {
                _wasAudioPlayingLastFrame = false;
                onInterviewerStopSpeaking?.Invoke();
            }

            if (!_isInterviewActive)
            {
                return;
            }

            _questionRemainingSeconds = Mathf.Max(0f, _questionRemainingSeconds - Time.deltaTime);
            UpdateTimerDisplay();
            onTimerUpdated?.Invoke(_questionRemainingSeconds, secondsPerQuestion);

            if (_questionRemainingSeconds <= 0f)
            {
                AdvanceToNextQuestion();
            }
        }

        /// <summary>
        /// Starts a brand new interview or resets an active/finished interview.
        /// Selects 4 questions and begins Question 1 with a 120-second limit.
        /// </summary>
        public void StartOrResetInterview()
        {
            StopQuestionAudio();
            _audioRequestVersion++;

            questionDatabase.EnsureLoaded();

            InterviewConfig config = new InterviewConfig
            {
                numberOfQuestions = Mathf.Max(1, questionsPerInterview),
                selectionMode = selectionMode
            };

            List<QuestionItem> builtQueue = questionDatabase.BuildSessionQueue(config);
            _selectedQuestions.Clear();
            _selectedQuestions.AddRange(builtQueue);

            if (_selectedQuestions.Count == 0)
            {
                Debug.LogError("[PracticeInterviewController] Cannot start interview: no questions found in QuestionDatabase.");
                _isInterviewActive = false;
                SetIdleStateUI("No questions found. Check Assets/Resources/questions.json.");
                return;
            }

            _currentQuestionIndex = -1;
            _isInterviewActive = true;

            if (startResetButtonLabel != null)
            {
                startResetButtonLabel.text = "Start/Reset Interview";
            }

            if (nextQuestionButton != null)
            {
                nextQuestionButton.interactable = true;
            }

            onInterviewStarted?.Invoke();
            AdvanceToNextQuestion();
        }

        /// <summary>
        /// Skips immediately to the next question when the player finishes answering
        /// under the 120-second time limit, or ends the interview after the last question.
        /// </summary>
        public void SkipToNextQuestion()
        {
            if (!_isInterviewActive)
            {
                return;
            }

            AdvanceToNextQuestion();
        }

        private void AdvanceToNextQuestion()
        {
            StopQuestionAudio();
            _audioRequestVersion++;

            int nextIndex = _currentQuestionIndex + 1;
            if (nextIndex >= _selectedQuestions.Count)
            {
                CompleteInterview();
                return;
            }

            _currentQuestionIndex = nextIndex;
            _questionRemainingSeconds = Mathf.Max(1f, secondsPerQuestion);

            QuestionItem current = _selectedQuestions[_currentQuestionIndex];
            UpdateQuestionDisplay(current);
            UpdateTimerDisplay();

            if (nextQuestionButtonLabel != null)
            {
                bool isLast = (_currentQuestionIndex == _selectedQuestions.Count - 1);
                nextQuestionButtonLabel.text = isLast ? "Finish Interview" : "Next Question";
            }

            onQuestionPresented?.Invoke(current, CurrentQuestionNumber, _selectedQuestions.Count);
            onTimerUpdated?.Invoke(_questionRemainingSeconds, secondsPerQuestion);

            PlayQuestionAudio(current.id, _audioRequestVersion);
        }

        private void CompleteInterview()
        {
            StopQuestionAudio();
            _audioRequestVersion++;

            int totalAsked = _selectedQuestions.Count;
            _isInterviewActive = false;
            _currentQuestionIndex = -1;
            _questionRemainingSeconds = 0f;

            if (nextQuestionButton != null)
            {
                nextQuestionButton.interactable = false;
            }

            if (nextQuestionButtonLabel != null)
            {
                nextQuestionButtonLabel.text = "Next Question";
            }

            if (statusHeaderText != null)
            {
                statusHeaderText.text = $"Interview Complete ({totalAsked}/{totalAsked})  |  Self-Reflection";
            }

            if (questionBodyText != null)
            {
                questionBodyText.alignment = TextAlignmentOptions.MidlineLeft;
                questionBodyText.text = completionReflectionText;
            }

            onTimerUpdated?.Invoke(0f, secondsPerQuestion);
            onInterviewCompleted?.Invoke();
        }

        private void PlayQuestionAudio(string questionId, int requestVersion)
        {
            if (string.IsNullOrWhiteSpace(questionId) || questionDatabase == null)
            {
                return;
            }

            questionDatabase.LoadQuestionAudio(questionId, clip =>
            {
                if (!_isInterviewActive || requestVersion != _audioRequestVersion)
                {
                    return;
                }

                if (clip != null && interviewerAudioSource != null)
                {
                    interviewerAudioSource.clip = clip;
                    interviewerAudioSource.Play();
                    _wasAudioPlayingLastFrame = true;
                    onInterviewerStartSpeaking?.Invoke(clip);
                }
            });
        }

        private void StopQuestionAudio()
        {
            if (interviewerAudioSource != null && interviewerAudioSource.isPlaying)
            {
                interviewerAudioSource.Stop();
            }

            if (_wasAudioPlayingLastFrame)
            {
                _wasAudioPlayingLastFrame = false;
                onInterviewerStopSpeaking?.Invoke();
            }
        }

        private void SetIdleStateUI(string customBody = null)
        {
            _isInterviewActive = false;
            _currentQuestionIndex = -1;
            _questionRemainingSeconds = secondsPerQuestion;

            if (startResetButtonLabel != null)
            {
                startResetButtonLabel.text = "Start/Reset Interview";
            }

            if (nextQuestionButton != null)
            {
                nextQuestionButton.interactable = false;
            }

            if (nextQuestionButtonLabel != null)
            {
                nextQuestionButtonLabel.text = "Next Question";
            }

            if (statusHeaderText != null)
            {
                statusHeaderText.text = $"Ready  |  {questionsPerInterview} Questions  |  {FormatTimeMMSS(secondsPerQuestion)} / Question";
            }

            if (questionBodyText != null)
            {
                questionBodyText.alignment = TextAlignmentOptions.Center;
                questionBodyText.text = customBody ?? "Press \"Start/Reset Interview\" to begin your verbal interview practice.";
            }
        }

        private void UpdateQuestionDisplay(QuestionItem question)
        {
            if (questionBodyText != null && question != null)
            {
                questionBodyText.alignment = TextAlignmentOptions.Center;
                questionBodyText.text = question.text;
            }
        }

        private void UpdateTimerDisplay()
        {
            if (statusHeaderText == null || !_isInterviewActive)
            {
                return;
            }

            statusHeaderText.text =
                $"Question {CurrentQuestionNumber} of {_selectedQuestions.Count}  |  {FormatTimeMMSS(_questionRemainingSeconds)}";
        }

        public static string FormatTimeMMSS(float timeInSeconds)
        {
            int clamped = Mathf.Max(0, Mathf.CeilToInt(timeInSeconds));
            int minutes = clamped / 60;
            int seconds = clamped % 60;
            return $"{minutes:00}:{seconds:00}";
        }
    }
}
