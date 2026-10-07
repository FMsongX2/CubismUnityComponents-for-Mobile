/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 모델의 Cubism 업데이트 컴포넌트를 실행 순서대로 모아 LateUpdate 한 번에 호출합니다.

using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering;
using System.Collections.Generic;
using UnityEngine;


namespace Live2D.Cubism.Framework
{
    [ExecuteInEditMode]
    public class CubismUpdateController : MonoBehaviour
    {
        private System.Action _onLateUpdate;

        /// 입력: 없음; 반환: 없음.
        public void Refresh()
        {
            var model = this.FindCubismModel();

            // 모델이 없으면 등록할 대상이 없으므로 현재 델리게이트 상태를 그대로 둡니다.
            if (model == null)
            {
                return;
            }

            // 재구성 전에 기존 호출 목록을 비워 같은 컴포넌트가 중복 등록되지 않게 합니다.
            _onLateUpdate = null;

            // 실행 순서로 정렬한 컴포넌트를 하나의 LateUpdate 델리게이트에 연결합니다.
            var components = model.GetComponents<ICubismUpdatable>();
            var sortedComponents = new List<ICubismUpdatable>(components);
            CubismUpdateExecutionOrder.SortByExecutionOrder(sortedComponents);

            foreach(var component in sortedComponents)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying && !component.NeedsUpdateOnEditing)
                {
                    continue;
                }
#endif

                _onLateUpdate += component.OnLateUpdate;
            }
        }

        #region Unity Event Handling

        /// 입력: 없음; 반환: 없음.
        private void Start()
        {
            Refresh();
        }

        /// 입력: 없음; 반환: 없음.
        private void LateUpdate()
        {
            // 구성된 순서대로 Cubism 후처리 업데이트를 실행합니다.
            if(_onLateUpdate != null)
            {
                _onLateUpdate();
            }
        }

        #endregion
    }
}
